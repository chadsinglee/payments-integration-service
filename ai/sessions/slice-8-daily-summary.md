# Session record: daily-summary (settlement summary query and endpoint)

Curated export of the AI-assisted work for this slice. No raw transcript, account names or local paths.

## Prompt (user, condensed)
- Build slice 8, `daily-summary`; keep the plan and architecture unchanged.
- Track the deferred items (lease expiry while waiting for the limiter, half-open probes, shared 429 cooldown, rate cap below 20, full compose smoke test) for final hardening.

## Design choices
- One committed query, `sql/daily-summary.sql`, with positional parameters (day, cutoff); embedded in Infrastructure and runnable in psql 16 via `\bind`.
- SAST day of Acme's own timestamp (`occurred_at`, fallback `received_at`); one outcome fact per payment, Approved over Declined; reversals/refunds per `transactionUUID`; gross figures.
- Late flag = received at or after the cutoff (day end + `Summary__CutoffHours`, default 12 h).
- Migration `V1_05` adds a partial expression index on the event day.

## Independent review and fixes
| Finding | Verdict | Action |
|---|---|---|
| A payment with Approved and Declined events counted as both | Accepted (Critical) | `DISTINCT ON` replaced by one outcome per payment, Approved preferred; test added |
| POST-response and RTTQ events had null `occurred_at`, so a next-day RTTQ resolution landed on the wrong day | Accepted (Critical) | `AcmePostResult.TransactionAt` parsed from `TransactionDateTime` and stored as `occurred_at` (not for observed reversals/refunds); tests added |
| Outcome lookup scanned every outcome event | Accepted | LATERAL lookup through `ix_payment_events_payment_id` |
| Over-refunds / refunds after reversal counted | Kept, documented | Acme reports the money as moved; rule in `SOLUTION.md`; test added |
| Missing tests (two currencies, over-refund, order of evidence) | Accepted | Added |
| `date=9999-12-31` caused a 500; culture-sensitive parse; `>` vs `>=` for late | Accepted | 400, invariant culture, `>=` |
| Approved amount can change after cutoff unflagged; index build blocks writes | Recorded | Known limits in `SOLUTION.md` |

## Validation
Build clean; 119 unit and 114 integration tests pass; `dotnet format --verify-no-changes` clean. No Docker Compose run (planned for hardening).

## What the assistant got wrong first
- Deduplicated per payment and event type instead of per payment, so Approved plus Declined double counted.
- Left POST/RTTQ events undated, which was invisible until the reviewer asked what day a next-day RTTQ approval lands on.
