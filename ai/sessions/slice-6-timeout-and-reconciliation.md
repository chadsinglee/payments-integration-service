# Session record: timeout-and-reconciliation (Acme client, RTTQ, recovery)

Curated export of the AI-assisted work for this slice. It is not a raw transcript: account names and local paths are removed, and the content is reduced to what was asked, decided, reviewed and changed. Earlier slices have no curated summary; their full transcripts are in `ai/transcripts/`.

## Prompt (user, condensed)
- Build slice 6, `timeout-and-reconciliation`, inside the approved architecture and plan.
- Reconciliation must preserve reversals when later callbacks arrive.
- Invalid authenticated callbacks must be logged safely and handled consistently with Acme's retry behaviour.
- Keep carried-forward items tracked in their slices: lease recovery (this slice), graceful shutdown, token `ToString` redaction, 401/429 handling.
- Payment POSTs are never retried automatically. Uncertain outcomes go to RTTQ; a resubmission reuses the `TransactionID` with a new `MessageID`.

## Context given to the assistant
`docs/spec.md` (timeouts, RTTQ), `SOLUTION.md` (decisions `worker-lease-claims`, `reconciliation-worker`, `rttq-404`, `pending-expiry`, `rttq-reversed-refunded-complete`, `auth-amount-source`), the slice-2 reference implementation, and the instruction packs.

## Task breakdown (assistant)
1. Migration `V1_03` (RTTQ attempts, recovery indexes, append-only `rejected_callbacks`).
2. Domain: `ReversalObserved` / `RefundObserved` events and rules.
3. Application: `RttqClassifier`, `ReconciliationService` (lease recovery, pending expiry, RTTQ reconcile), guarded `ApplyIfAsync`.
4. Infrastructure: `AcmeClient.QueryTransactionAsync`, store claim and sweep queries.
5. Api: store and log rejected callbacks (no body in logs); still 400/403.
6. Worker: `ReconciliationWorker`, settings validation.
7. Stub: RTTQ endpoint, admin endpoints, magic amount `99502`.
8. Tests: unit (rules, classifier) and integration (timeout, processing, decline, resend in/after window, expiry, lease recovery, amount fetch, reversal preservation, rejected callbacks).

## Independent review (fresh-context reviewer, then fixes)
| Finding | Verdict | Action |
|---|---|---|
| Pending-expiry scan could mark an already-sent payment `Failed` (race with the claim) | Accepted (High) | `ApplyEventAsync` takes a precondition checked under the row lock; expiry uses `ApplyIfAsync`; test added |
| RTTQ `Refunded` left an `Unknown` payment unresolved and never supplied `AuthAmount` | Accepted | Refund evidence implies approval (flagged); RTTQ amount is accepted from observed events; tests added |
| `Exhausted` never cleared after a later approval; `giveUp` used a stale status | Accepted | Status change resets `reconciliation_state`; give-up decided under the lock; test added |
| Rejected-callback storage could fail on NUL or a split surrogate pair | Accepted | Sanitised and cut on a safe boundary |
| RTTQ evidence key was a random GUID; repeated `ALERT` on already-flagged payments | Accepted | Key is `rttq:{paymentId}:{attempt}`; alert only when the flag first turns on |
| Sweeps ran in every loop | Accepted | Only loop 0 sweeps |

## Validation
`dotnet build`, 99 unit and 88 integration tests (Testcontainers Postgres), `dotnet format --verify-no-changes`.

## What the assistant got wrong first (and how it was caught)
- Pending expiry initially had no check under the lock; the reviewer found the race and a test now pins it.
- The first RTTQ-`Refunded` handling dropped the evidence; found in review, fixed with rules plus an integration test.
