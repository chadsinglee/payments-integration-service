# Session record: post-slice-hardening

Curated summary of the AI-assisted work after slice 9. It is a summary, not a transcript. The raw transcripts in `ai/transcripts/` end on 2026-10-21 at 10:20 and do not cover this work. No account names or local paths.

## What was done, in order
1. **Late-webhook sweep.** A reversal or refund webhook that arrives before its payment is retried against `order_number` (batch of 50, gives up after 12 attempts or 60 minutes with an `ALERT`) and adopted through the shared outcome path. Migration `V1_06` lets `payment_events` change only in that one way. A late event that contradicts the payment is downgraded to `Informational` and the payment is flagged.
2. **`AuthAmount` guard.** An authorised amount above the requested amount is not stored, the payment is flagged, and one `ALERT` is logged. After review, the POST-response path was changed to mark reconciliation `Exhausted` at once, as the RTTQ path does, with a test for it.
3. **Logging pass (no behaviour change).** Same property names everywhere (`PaymentId`, `TransactionId`, `OrderNumber`, `MessageId`, `AttemptCount`, `Source`, `DedupKey`); one Information line per state transition with old state, new state and reason; Warning for 429, unmatched events, conflicts handling and limiter problems; Error only for `ALERT` lines. `LoggingTests` captures a full submit, timeout, RTTQ, approved flow and asserts the card token, API key and webhook secret never appear. JSON console logs in Compose. Polly set to `Critical` (it only logs its own timeout, which duplicates our Warning).
4. **Pull request and independent review.** PR #1 was opened and reviewed by a fresh-context reviewer. Three fixes were applied as separate commits: the POST-path `AuthAmount` guard, the transaction-level Postgres advisory lock for the sweep (several workers stay safe), and the PR description. Five lower-priority points were deferred (`SOLUTION.md` §15).
5. **Flaky-test investigation.** The full integration suite was run 10 times on the tip (1 failure) and 10 times before the sweep lock (0 failures). The lock was not the cause. The failing test depended on the app clock and the database clock agreeing, and a second test used a fixed sleep. The tests were changed to poll for a condition with a timeout, and `Sweep_WhenTwoWorkersSweepAtOnce` now asserts the final state.
6. **SQL-side scheduling.** Scheduled retry times (`next_attempt_at`, the 429 `Retry-After` reschedule, the reconciliation back-off) are computed in the database as `now() + delay`, so writes and claims share one clock. Delay values are unchanged. A new test sets the app clock ahead of the database and shows an immediate resend is still claimable at once; it was confirmed to fail on the old code. Ten consecutive full integration runs then passed.
7. **Merge and final checks.** PR #1 was merged into `main` with a regular merge commit. On `main`: fresh Compose stack, smoke test, build, 155 unit and 160 integration tests, `dotnet format`, and a 300-payment simulation (no payment left non-terminal, no payment with more than one `TransactionID`, one `ALERT` per exhausted payment). A scan found no secrets or real card data.

## What I (the human) decided
- No blind resend, and a bounded resend window.
- Conflicts are logged at Error as `ALERT`, because they flag a payment for a person.
- Not to add metrics (CloudWatch metric filters and EMF are proposed in `SOLUTION.md` §8, not built).
- Which review findings to fix now and which to defer.
- Not to fix the two remaining app-clock reads now (`SOLUTION.md` §15).

## Where the evidence is
`SOLUTION.md` §13 (each fix with its regression test), `SOLUTION.md` §15 (follow-ups), `ai/spec-conformance.md` (test counts), PR #1.
