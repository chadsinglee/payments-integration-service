# Payments Integration Service

A .NET 8 service that lets the Zapper app submit and track card payments through the PSP Acme Pay (stored card tokens, facilitator sub-merchants). It processes Acme's Payment Advice notifications and Transaction Status webhooks safely (duplicates, out-of-order, late reversals/refunds) and produces a daily per-sub-merchant settlement summary. An Acme stub, a Postgres database and a Redis rate limiter run alongside it in Docker Compose.

> Status: complete (all nine slices built and verified); see [`SOLUTION.md`](SOLUTION.md) for design and the deliverables checklist.

## Run locally
```powershell
docker compose up --build -d      # Postgres, Redis, Acme stub, Api (:5001), Worker, stub (:5002)
curl -X POST http://localhost:5001/payments -H "Content-Type: application/json" -d '{"paymentId":"pay-1","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":12500}'
curl http://localhost:5001/payments/pay-1
docker compose down -v
```
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

### Daily summary
`GET /summary?date=2026-03-01` (a SAST calendar day) returns one row per sub-merchant and currency with approved, declined, reversal and refund counts and totals in cents, plus `lateCount`/`lateAmountMinor` (the part received after the cutoff), `cutoffAt` and `settled`. Figures are gross (a later-reversed approval still counts as approved on its day), each payment counts once as approved and once as declined, and reversals/refunds count once per webhook `transactionUUID`.

The same query is committed as `sql/daily-summary.sql` and can be run directly in psql 16: replace its final `;` with `\bind '2026-03-01' '2026-03-02 12:00:00+02' \g` (the day, then the cutoff).
### Demo data, stub and callbacks
Seed data (`Database__SeedDemoData=true`): customers `cust-001`, `cust-002`; sub-merchants `ZAP0004182`, `ZAP0004183`. Example: `POST /payments` with `{"paymentId":"pay-1","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":12500}` (amount in cents) returns 202; `GET /payments/pay-1` shows the status. Stub magic amount `99503` returns a 503 (ends `Unknown`); `99502` fails only the first POST per `TransactionID` (RTTQ 404, then resend); `99429` answers the first POST per `TransactionID` with 429 and `Retry-After: 1` (the payment returns to `Pending` and is resent); `99035` answers after 35 s (a client timeout, then RTTQ finds the approval). Stub admin: `POST /admin/rttq-processing/{transactionId}?count=n` and `POST /admin/outcomes/{transactionId}/status?value=Reversed|Refunded|Complete` (what RTTQ reports). To make the stub call the Api back, use `POST http://localhost:5002/admin/callbacks/{transactionId}?send=<script>`, where the script is a comma-separated list of `advice`, `reversal` and `refund[:amountMinor]`, sent in that order with the Api's real Basic auth and HMAC signature (Compose wires this up). Identical items in one script are byte-identical redeliveries (duplicates); a different order demonstrates out-of-order delivery. Example, after payment `pay-1` is approved: `curl -X POST "http://localhost:5002/admin/callbacks/<transactionId>?send=advice,advice,refund:3000,refund:3000"`, then `GET /payments/pay-1` shows `PartiallyRefunded`. The transaction id is in the `payments.transaction_id` column. Outside Compose, set `Stub__CallbackBaseUrl` (and `Stub__MerchantId`, `Stub__AdviceUsername`, `Stub__AdvicePassword`, `Stub__WebhookSecret` to match the Api); without it the endpoint answers 409.

### Operations
- Logs: `docker compose logs -f worker api`. Payment processing lines carry `PaymentId` and `TransactionId`. API keys, card tokens, emails and connection strings are never logged.
- Shutdown: `docker compose stop worker` lets an in-flight Acme call finish and be recorded (host timeout = Acme timeout + 15 s, `stop_grace_period: 60s`). A hard kill is recovered through lease expiry and RTTQ.
- Reset: `docker compose down -v`.

## Build and test
See the Commands table in [`AGENTS.md`](AGENTS.md) (unit tests need no infrastructure; integration tests need Docker).

## Documents
- [`docs/spec.md`](docs/spec.md): the authoritative specification
- [`SOLUTION.md`](SOLUTION.md): design, decisions, domain constraints, diagram, deliverables checklist
- [`ai/`](ai/): AI workflow artifacts (plans, prompts, review notes)
- [`docs/ai-template.md`](docs/ai-template.md): the AI repo-starter template this repo was bootstrapped from
