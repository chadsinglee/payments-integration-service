# Payments Integration Service

A .NET 8 service that lets the Zapper app submit and track card payments through the PSP Acme Pay (stored card tokens, facilitator sub-merchants). It processes Acme's Payment Advice notifications and Transaction Status webhooks safely (duplicates, out-of-order, late reversals/refunds) and produces a daily per-sub-merchant settlement summary. An Acme stub, a Postgres database and a Redis rate limiter run alongside it in Docker Compose.

> Status: complete (all nine slices built and verified); post-slice fixes are listed in [`SOLUTION.md` §13](SOLUTION.md#13-fixes-after-delivery), and known risks and follow-ups in §14. See `SOLUTION.md` for design and the deliverables checklist.

## Run locally
```powershell
docker compose up --build -d      # Postgres, Redis, Acme stub, Api (:5001), Worker, stub (:5002)
curl -X POST http://localhost:5001/payments -H "Content-Type: application/json" -d '{"paymentId":"pay-1","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":12500}'
curl http://localhost:5001/payments/pay-1
docker compose down -v
```
The `curl` examples assume PowerShell 7 or a bash shell (in Windows PowerShell 5.1, `curl` is an alias for `Invoke-WebRequest`).
Compose uses throwaway local defaults (override with `POSTGRES_PASSWORD`, `ACME_API_KEY`, `ACME_MERCHANT_ID`, `SEED_DEMO_DATA`). Redis holds the shared Acme rate limit (the worker pauses, and calls nothing, while Redis is down). Outside Compose, configuration is environment-only (never committed):

| Variable | Host | Notes |
|---|---|---|
| `ConnectionStrings__Payments` | Api, Worker | Required. Postgres connection string. `Maximum Pool Size` defaults to 40 per process (Api plus Worker stay under Postgres' 100 connections); set it in the string to override |
| `Acme__BaseUrl`, `Acme__ApiKey`, `Acme__MerchantId` | Worker | Required. Startup fails naming missing keys, never printing values |
| `Acme__TimeoutSeconds` | Worker | Default 40 (Acme can hold a request up to 30 s); must be > 0 |
| `Acme__MerchantId`, `Acme__AdviceUsername`, `Acme__AdvicePassword`, `Acme__WebhookSecret` | Api | Required for `POST /acme/advice` (HTTP Basic + `MerchantID` match) and `POST /acme/webhook` (`x-signature` HMAC-SHA256). Startup fails naming missing keys. Compose defaults are local-only placeholders |
| `Worker__Concurrency`, `Worker__PollIntervalMs`, `Worker__LeaseMarginSeconds` | Worker | Defaults 4, 500, 30; all >= 1 |
| `Reconciliation__ResendWindowMinutes`, `PendingExpiryMinutes`, `MaxAttempts`, `BackoffSeconds`, `BackoffCapSeconds` | Worker | Defaults 10, 15, 8, 10, 300; all >= 1 (cap >= backoff). Resend window: an RTTQ 404 within it resubmits with the same `TransactionID` (the first resend at once, later ones with backoff, and no more than `MaxAttempts`) |
| `Reconciliation__UnmatchedMaxAttempts`, `Reconciliation__UnmatchedMaxAgeMinutes` | Worker | Defaults 12, 60; both >= 1. A reversal/refund webhook that arrived before its payment is retried against `order_number` until it matches (then applied once) or either limit is hit (then it stops and logs an `ALERT`) |
| `ConnectionStrings__Redis` | Worker | Required. Redis for the shared Acme rate limit (POST and RTTQ combined) |
| `Acme__RateLimitPerSecond` | Worker | Default 15, range 1-19 (Acme rejects more than 20 per second across all workers; 20 or more is refused at startup). A 429 also sets a Redis cooldown honoured by every worker |
| `Throttle__FailureThreshold`, `BreakerOpenSeconds`, `UnauthorizedPauseSeconds` | Worker | Defaults 5, 30, 60; all >= 1. Breaker opens after that many consecutive 5xx/timeouts, then admits one probe call before closing; a 401 pauses for the unauthorized period |
| `Database__MigrateOnStartup`, `Database__SeedDemoData` | Api | Defaults true, false |
| `Summary__CutoffHours` | Api | Default 12, range 0-720. A SAST day is final this long after it ends; later events still count on their own day and are reported as late |
| `Stub__ApiKey` | Acme.Stub | Default `sk_test_stub` |
| `Stub__CallbackBaseUrl`, `Stub__MerchantId`, `Stub__AdviceUsername`, `Stub__AdvicePassword`, `Stub__WebhookSecret` | Acme.Stub | Where and how the stub sends callbacks (must match the Api's `Acme__*` values; set in Compose) |

### Guided demo
With the stack running, `./ai/demo.ps1` walks one payment flow end to end in the console and prints PASS/FAIL for each expectation (Windows PowerShell 5.1 or PowerShell 7):
```powershell
./ai/demo.ps1              # full run, about 1 minute
./ai/demo.ps1 -Pause       # wait for Enter between steps, for presenting
./ai/demo.ps1 -SkipSlow    # skip the 35 s payment, about 15 seconds
```
It covers: 202 plus `Location` and the worker's live state changes, idempotency (200 and 409) and validation, a decline by response code, a 429 retry and an unknown outcome resolved by RTTQ then a resend (both with the same `TransactionID`, shown from the stub's received POSTs), duplicate and out-of-order callbacks, rejected unauthenticated callbacks, and the daily summary checked against what the run submitted. It exits with code 1 on any failure. Run one copy at a time: the summary check compares totals before and after, so other traffic on the same sub-merchant skews it. The event log and callback steps need Docker access to the Postgres container and are skipped without it.

### Daily summary
`GET /summary?date=2026-03-01` (a SAST calendar day) returns one row per sub-merchant and currency with approved, declined, reversal and refund counts and totals in cents, plus `lateCount`/`lateAmountMinor` (the part received after the cutoff), `cutoffAt` and `settled`. Figures are gross (a later-reversed approval still counts as approved on its day), each payment counts once as approved and once as declined, and reversals/refunds count once per webhook `transactionUUID`.

The same query is committed as `sql/daily-summary.sql` and can be run directly in psql 16: replace its final `;` with `\bind '2026-03-01' '2026-03-02 12:00:00+02' \g` (the day, then the cutoff).

### Demo data, stub and callbacks
Seed data (`Database__SeedDemoData=true`): customers `cust-001`, `cust-002`; sub-merchants `ZAP0004182`, `ZAP0004183`. Example: `POST /payments` with `{"paymentId":"pay-1","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":12500}` (amount in cents) returns 202; `GET /payments/pay-1` shows the status. Stub magic amount `99503` returns a 503 (ends `Unknown`); `99502` fails only the first POST per `TransactionID` (RTTQ 404, then resend); `99429` answers the first POST per `TransactionID` with 429 and `Retry-After: 1` (the payment returns to `Pending` and is resent); `99035` answers after 35 s (within the default 40 s client timeout, so the payment is approved from that response; set `Acme__TimeoutSeconds` below 35 to see a timeout and an RTTQ lookup). Stub admin: `POST /admin/rttq-processing/{transactionId}?count=n` and `POST /admin/outcomes/{transactionId}/status?value=Reversed|Refunded|Complete` (what RTTQ reports). To make the stub call the Api back, use `POST http://localhost:5002/admin/callbacks/{transactionId}?send=<script>`, where the script is a comma-separated list of `advice`, `reversal` and `refund[:amountMinor]`, sent in that order with the Api's real Basic auth and HMAC signature (Compose wires this up). Identical items in one script are byte-identical redeliveries (duplicates); a different order demonstrates out-of-order delivery. Example, after payment `pay-1` is approved: `curl -X POST "http://localhost:5002/admin/callbacks/<transactionId>?send=advice,advice,refund:3000,refund:3000"`, then `GET /payments/pay-1` shows `PartiallyRefunded`. The transaction id is in the `payments.transaction_id` column. Outside Compose, set `Stub__CallbackBaseUrl` (and `Stub__MerchantId`, `Stub__AdviceUsername`, `Stub__AdvicePassword`, `Stub__WebhookSecret` to match the Api); without it the endpoint answers 409.

### Operations
- Logs: `docker compose logs -f worker api`. Payment processing lines carry `PaymentId` and `TransactionId`. API keys, card tokens, emails and connection strings are never logged.
- Shutdown: `docker compose stop worker` lets an in-flight Acme call finish and be recorded (host timeout = Acme timeout + 15 s, `stop_grace_period: 60s`). A hard kill is recovered through lease expiry and RTTQ.
- Reset: `docker compose down -v`.
- Acme call timing: set `ACME_GATE_LOG_LEVEL=Debug` (default `Information`) before `docker compose up` to log, for every Acme call, how many ms passed between Redis granting its rate-limit token and the call being dispatched. It is per-call detail, so noisy; use it only to diagnose rate-limit timing.
- Smoke test: [`ai/demo.ps1`](ai/demo.ps1) automates most of the Compose smoke test (approve, decline, 429 retry, unknown outcome and resend, summary, callbacks). Graceful stop and hard kill remain a manual procedure.

## Build and test
See the Commands table in [`AGENTS.md`](AGENTS.md) (unit tests need no infrastructure; integration tests need Docker).

`dotnet test tests/Payments.CustomerSimulation` is a black-box suite that talks HTTP to the running Compose stack (`PAYMENTS_API_URL`, default `http://localhost:5001`); see [`docs/customer-simulation-spec.md`](docs/customer-simulation-spec.md) and [`docs/simulation-traceability.md`](docs/simulation-traceability.md).

## Documents
- [`docs/spec.md`](docs/spec.md): the authoritative specification
- [`SOLUTION.md`](SOLUTION.md): design, decisions, domain constraints, diagram, deliverables checklist
- [`ai/spec-conformance.md`](ai/spec-conformance.md): requirement-by-requirement traceability to the code
- [`ai/`](ai/): AI workflow artifacts (plans, prompts, review notes). The session transcripts in `ai/transcripts/` end on 2026-10-21 at 10:20; the later work (logging pass, `AuthAmount` guard, sweep lock, flaky-test investigation, SQL-side scheduling, PR review) is covered in `ai/sessions/post-slice-hardening.md`.
- [`docs/ai-template.md`](docs/ai-template.md): the AI repo-starter template this repo was bootstrapped from
