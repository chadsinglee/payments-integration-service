# Session record: throughput-protection (rate limit, breaker, 401, 429)

Curated export of the AI-assisted work for this slice, reduced to what was asked, decided, reviewed and changed. No raw transcript, account names or local paths.

## Prompt (user, condensed)
- Build slice 7, `throughput-protection`, keeping the plan and architecture unchanged unless a genuine technical issue forces it.
- Add a full Docker Compose smoke test to the final hardening slice, because slice 6 had none.
- Payment POSTs are never retried automatically; uncertain outcomes go to RTTQ; a resubmission reuses the `TransactionID` with a new `MessageID`.

## Context given to the assistant
`docs/spec.md` (20 req/s for POST and RTTQ combined, 429 `Retry-After`, 401/E02), `SOLUTION.md` (decisions `global-rate-limit`, `acme-rate-limit`, `acme-client-resilience`, `acme-401`, section 3.2), the slice 2 and 6 code, and the instruction packs.

## Task breakdown (assistant)
1. Application: `AcmeGate` (breaker, 401/429/limiter-unavailable pause), `IAcmeRateLimiter`, release paths in `PaymentSubmissionService` and `ReconciliationService`.
2. Infrastructure: `RedisAcmeRateLimiter` (sliding window in a Lua script), `Retry-After` parsing, `ReleaseClaimAsync` and `ReleaseReconciliationAsync`.
3. Worker: gate and limiter configuration, fail-fast validation, compose and README updates.
4. Stub: magic amounts `99429` (429 on first POST) and `99035` (35 s delay).
5. Tests: gate unit tests; Redis limiter (Testcontainers); 429, 401, breaker, Redis-down, RTTQ 429, API stays quick.

## Design choices
- Claim then take a token, releasing without counting an attempt on denial, so idle polling spends no budget.
- One shared pause (breaker, 401, 429, Redis down) checked before claiming.
- Sliding window rather than token bucket; default 18/s under the 20/s limit.
- Breaker is per process (not shared via Redis); recorded as an assumption.

## Independent review (fresh-context reviewer, then fixes)
| Finding | Verdict | Action |
|---|---|---|
| A long 401 or Redis outage let pending-expiry mark payments `Failed` (release cleared `first_sent_at`) | Accepted (Critical) | Migration `V1_04` adds `deferred_at`, set on release and excluded from the expiry scan; the sweep is skipped while the gate is paused; tests added |
| Lease recovery wrote its event without checking status (dedup key could recur after a release lowered the attempt) | Accepted | Uses `ApplyIfAsync` with a `Submitted` precondition |
| Breaker or pause not re-checked after the claim or limiter wait | Accepted | `AcquireAsync` checks the pause on every pass and denies, so the claim is released |
| Non-cancellation exceptions between claim and POST left the claim held | Accepted | Any exception releases the claim (limiter and `BuildRequest`), then rethrows; test added |
| `IHttpClientFactory` could log the `Authorization` header at Trace on .NET 8 | Accepted | `RedactLoggedHeaders(Authorization)` |
| Options `ToString()` printed the Redis connection string | Accepted | Overridden |
| Half-open lets every loop probe at once; lease does not cover the limiter wait; per-process 429 pause; rate cap of 20 | Deferred | Recorded as known limits; the default (18/s) and 70 s lease make them narrow |

## Validation
`dotnet build`, 117 unit and 102 integration tests, `dotnet format --verify-no-changes`. No Docker Compose run yet (planned in the hardening slice).

## What the assistant got wrong first
- A test asserted PascalCase JSON from the stub, which emits camelCase.
- A per-window timestamp assertion on the Redis limiter was flaky under parallel tests (client jitter); changed to a total-grants assertion.
