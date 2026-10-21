# Session record: hardening-and-deliverables

Curated export of the AI-assisted work for this slice. No raw transcript, account names or local paths.

## Prompt (user, condensed)
- A worker must not submit a payment if its lease expired while it waited for the rate limiter, or another worker took over.
- Finish the half-open breaker and a shared 429 cooldown; keep the global rate limit configurable, default 18 per second, below Acme's 20.
- Check graceful shutdown and that no token, API key or secret is logged.
- Run the full Docker Compose smoke test, the whole suite and `dotnet format --verify-no-changes`; update README, `SOLUTION.md` and AI records. Commit nothing.

## Design choices
- `ExtendClaimAsync` / `ExtendReconciliationAsync`: a conditional update (same status and attempt, `lease_until > now()`) run right after the limiter grants a token. It both revalidates ownership and restarts the lease. A lost claim means no call; lease recovery resolves it via RTTQ.
- Breaker: one probe after the open period; success closes, failure re-opens, other workers wait. 429 publishes a Redis cooldown (only ever lengthened) checked in the limiter's Lua script. Rate cap 1-19.
- Shutdown: host timeout = Acme timeout + 15 s, both workers stopped concurrently, compose `stop_grace_period: 60s`.
- Redaction: `ToString` overrides on secret-carrying records; `PaymentId`/`TransactionId` log scopes.

## Independent review and fixes
| Finding | Verdict | Action |
|---|---|---|
| Hosted services stop one after another by default, so the submission worker kept starting POSTs while the reconciliation worker drained and could be cut off | Accepted (bug) | `ServicesStopConcurrently = true`; asserted in the DI test |
| A failed probe only re-opened the breaker if the failure count reached the threshold; a stale success before it reset the count; the probe slot was freed before the pause was set | Accepted (bug) | A failed probe trips directly; the slot is freed after any trip; regression test added |
| Acme call could start after shutdown began during the limiter wait | Accepted | Cancellation checked before the extend; the claim is released |
| Everything else (lease SQL, Lua scripts, probe slot leaks, redaction, Dockerfile `COPY sql`) | Verified correct | None |

## Validation
- Build clean; 134 unit and 124 integration tests pass; `dotnet format --verify-no-changes` clean.
- Compose smoke test (all five services healthy): approved, declined (99005) and 429-then-approved (99429) payments; `GET /summary` totals match; logs scanned for the API key, card token, email and `Bearer`, none found; `docker compose stop worker` finished in 1 s with "Application is shutting down".
- The first compose build failed: the Dockerfiles did not copy `sql/`, which the Infrastructure project embeds. Fixed in all three.

## What the assistant got wrong first
- The Dockerfiles broke when the summary slice embedded `sql/daily-summary.sql`; no compose run happened until hardening.
- Relied on the host's default one-at-a-time stop order, so the "in-flight call is recorded" promise did not hold until the reviewer flagged it.
- The first probe logic depended on a failure counter that a stale success could reset.

## Follow-up verification (after the slice 9 review)
- Added stub callback emission (`src/Acme.Stub/CallbackEmitter.cs`, `POST /admin/callbacks/{tx}?send=...`) and tests (`CallbackEmitterTests`).
- Compose, fresh database: `advice,advice,refund:3000,refund:3000` gave `PartiallyRefunded` with one advice and one refund event; `refund:4000,advice,refund:6000` (out of order) gave `Refunded`; `reversal,advice,advice` stayed `Reversed`; no `event_conflicts`; summary refunds 3 / 13000, reversals 1 / 10000.
- Stub bug found by this run: each webhook carried the current time, so a "duplicate" differed and the service correctly flagged a conflict. The stub now uses one timestamp per script.
- Slow-call stop: a 35 s payment was in flight; `docker compose stop worker` took 27 s, the payment ended `Approved` with its interaction recorded.
- Hard kill mid-call: restarted worker recovered the payment through lease expiry and RTTQ (`Approved` after about 60 s); the stub saw one POST for it (no resend).
- Log scope test: `SubmitNext_WhenApproved_LogsTheOutcomeInsideThePaymentAndTransactionScope`. Final gate: 134 unit, 128 integration, format clean.

## Not verified / deferred
- No test of the pooled-connection idle timeout setting (50 s, below Acme's 60 s).
- Callback-before-worker-response is covered by integration tests, not run through Compose.
- The 99035 and kill scenarios are manual Compose checks, not automated tests.
