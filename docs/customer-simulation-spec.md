# Customer simulation tests — spec

## Goal
A black-box test project (`tests/Payments.CustomerSimulation`) that behaves like the Zapper app and like Acme: it only
talks HTTP to the **running** stack (`docker compose up --build -d`). No host-in-process, no database access. It proves the
deployed system works end to end, including the worker, the stub and the callbacks.

## Not covered here
Unit/integration tests already cover internals (leases, status-rule orderings, HMAC details, SQL). This project covers
what a customer or Acme can observe.

## Setup
| Setting | Env var | Default |
|---|---|---|
| Payments API | `PAYMENTS_API_URL` | `http://localhost:5001` |
| Acme stub | `ACME_STUB_URL` | `http://localhost:5002` |
| Webhook secret | `ACME_WEBHOOK_SECRET` | `local-webhook-secret` |

Run: `dotnet test tests/Payments.CustomerSimulation`. If the stack is unreachable every test fails with one clear message.
Seed data used: customers `cust-001`, `cust-002`; sub-merchants `ZAP0004182`, `ZAP0004183`.

Each test uses a unique payment id and a unique amount, so the stack can be reused across runs. The stub's magic amounts
(`99005`, `99051`, `99503`, `99502`, `99429`) select outcomes. Tests wait by polling `GET /payments/{id}` (30 s limit; 120 s
for scenario 7, which waits for reconciliation backoff).

Known caveat: scenario 6 leaves a payment retrying against the stub for ~10 minutes (about 7 attempts with backoff). Many
back-to-back runs still leave payments in flight; wait a few minutes or `docker compose down -v` to reset.

## Scenarios
| # | Customer story | Expected |
|---|---|---|
| 1 | Pays R125.00 | `202`, `Pending`, then `Approved` with `authAmount` = amount |
| 2 | Card declined (do-not-honour, insufficient funds) | ends `Declined`, never `Approved` |
| 3 | App retries the same request | `200`, same payment, Acme charged once |
| 4 | App reuses a payment id with a different amount | `409` |
| 5 | Bad input / unknown customer / unknown sub-merchant / unknown payment | `400` / `404` / `404` / `404` |
(the worker keeps retrying the same `TransactionID`, with backoff, for its 10 min resend window)
| 7 | Acme fails once then recovers; Acme rate-limits once | still ends `Approved`, never a second `TransactionID` |
| 8 | 20 customers pay at once | all `Approved`, each charged exactly once |
| 9 | Partial then full refund | `PartiallyRefunded`, then `Refunded` |
| 10 | Duplicate refund delivery | applied once |
| 11 | Reversal | `Reversed` |
| 12 | Forged callbacks (no/wrong Basic auth, bad signature) | `401`, payment untouched |
| 13 | Daily summary after a mix of approved and declined payments | sub-merchant totals rise by the right count and amount |

## Traffic simulation (`Traffic/`, `Category=Traffic`)
Paced, weighted customer traffic (seeded, so a run can be repeated with `SIM_SEED=<n>`), followed by a three-way reconciliation.

| Mix | % | Expected end state |
|---|---|---|
| Approve | 70 | `Approved`, `authAmount` = amount |
| Decline | 10 | `Declined` |
| Retry storm (3 concurrent retries) | 5 | `Approved`; retries return 200 |
| Partial / full / duplicate refund | 2 / 2 / 2 | `PartiallyRefunded` / `Refunded` / `PartiallyRefunded` |
| Reversal | 3 | `Reversed` |
| Acme blip (503 once, 429 once) | 3 | `Approved` after recovery |
| Acme outage (503) | 2 | `Unknown`/`Pending`/`Submitted` (worker keeps resending), never a final outcome |
| Slow Acme (35 s) | 1 | `Approved` |

Reconciliation after the run (any mismatch fails the test and prints a report with per-scenario latency p50/p95/max):
1. **API:** every payment's status and `authAmount` match the ledger; none flagged `needsInvestigation` (outage excepted).
2. **Acme:** each payment reached Acme under exactly one `TransactionID` (outage: at least, as earlier runs' resends share the amount).
3. **Summary:** per sub-merchant, approved/declined/reversal/refund counts and amounts rose by exactly the ledger's totals.

| Test | Length | Runs |
|---|---|---|
| `ShortRun_…` | 60 s, 5/s (~300 payments) | by default |
| `MediumRun_…` | 5 min, 5/s (~1500) | `SIM_MEDIUM=1` |
| `ChaosRun_…` | 60 s, 5/s, `docker compose restart worker` a third of the way in | `SIM_CHAOS=1` (needs the docker CLI) |

## Spec compliance (`Scenarios/SpecComplianceTests.cs`)
Requirements from `docs/spec.md` that only show in a running system. Mapping of every requirement to its proof: `docs/simulation-traceability.md`.
- Webhook (refund / reversal) arriving during Acme's 35 s hold is applied once the approval lands (out of order).
- Burst of 150 payments: all approve, accepted <5 s, Acme never sees more than 20 posts in any second.
- Responses never contain the card token or email; fractional amount → 400.
- Callback hardening: extra JSON fields acknowledged, wrong merchant 403, non-JSON 400, oversized 413, unknown rrn webhook acknowledged twice.
- `SIM_DOCKER=1`: container logs contain no card token or secret.
