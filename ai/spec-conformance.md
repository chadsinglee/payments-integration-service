# Spec conformance (final check)

Produced by an independent read-only review of `docs/spec.md` against `SOLUTION.md`, the plan, `src/` and `tests/`, then corrected by the build owner. Build, 155 unit and 156 integration tests, and `dotnet format --verify-no-changes` were run after the last change (post-review fixes: webhook amount parsing, reversal/refund validation, PEND/COMPL classification, audit email redaction, localhost-only Compose ports); the Docker Compose smoke test passed before those fixes and was not re-run (see `ai/sessions/slice-9-hardening.md`).

## Functional requirements
| # | Requirement | Code | Proof | Status |
|---|---|---|---|---|
| F1 | Submit payment | `PaymentEndpoints.cs`, `PaymentService.cs` | `SubmitAndApproveTests` | met |
| F2 | Acme request from sub-merchant and card | `CardAcceptorName.cs`, `AcmeClient.cs` | `DomainBasicsTests`, `AcmeClientTests` | met |
| F3 | Query payment | `GET /payments/{id}` | `SubmitAndApproveTests` | met |
| F4 | Approved | `PaymentSubmissionService`, `AcmeOutcomeClassifier` | `SubmitPayment_WhenAcmeApproves_*` | met |
| F5 | Declined | `AcmeOutcomeClassifier` | `DeclinesAndDuplicatesTests` | met |
| F6 | Duplicate app payment id | unique constraint, `PaymentService` | `DeclinesAndDuplicatesTests` | met |
| F7 | Duplicate notification | `CallbackService`, unique `(source, dedup_key)` | `Advice_WhenDeliveredTwice_*`, `Webhook_WhenDeliveredTwice_*` | met |
| F8 | Out-of-order notifications | `PaymentStatusRules` | `PaymentStatusRulesTests`, callback race tests | met |
| F9 | Reversal after approval | `PaymentStatusRules` | `Webhook_WhenReversalFollowsApproval_*` | met |
| F10 | Refund after approval | `PaymentStatusRules` | `Webhook_WhenRefundsAddUpToTheAuthAmount_*` | met |
| F11 | Acme timeout | `AcmeClient`, `ReconciliationService` | `ReconciliationTests`, `AcmeClientTests` | met |
| F12 | Acme stub (Appendix A) | `src/Acme.Stub` | `StubTests` | met |
| F13 | Task 1 assumptions and stub behaviours | `SOLUTION.md` 9 | n/a | met |
| F14 | Schema and migrations | `Schema/v1/V1_00`-`V1_05` | `SchemaTests` | met |
| F15 | Daily summary + committed query | `sql/daily-summary.sql`, `SummaryEndpoints.cs` | `DailySummaryTests` | met |
| F16 | Data model explanation | `SOLUTION.md` 7 | n/a | met |
| F17 | Public callbacks with Basic and HMAC auth | `CallbackEndpoints.cs` | callback auth tests | met |
| F18 | Seed data | `DatabaseSeeder.cs` | test harness; compose smoke | met |

## Non-functional requirements and deliverables
| # | Item | Status | Note |
|---|---|---|---|
| N1 | .NET 8, SQL database | met | |
| N2 | Environment configuration | met | startup tests |
| N3 | Concurrency-safe Acme calls, no degradation | met | `ThroughputProtectionTests`, `AcmeGateTests`, `RedisRateLimiterTests` |
| N4 | Logging and diagnostics | met | `PaymentId`/`TransactionId` scope asserted by `SubmitNext_WhenApproved_LogsTheOutcomeInsideThePaymentAndTransactionScope` and seen in compose logs |
| N5 | Tests | met | |
| N6 | Containerised, runs locally | met | full compose smoke test passed in slice 9 |
| N7 | README | met | |
| N8 | AWS approach incl. graceful shutdown | met | `SOLUTION.md` 8; shutdown settings tested |
| D1 | Source code | met | |
| D2 | `SOLUTION.md` complete | met | sections 1-11 |
| D3 | AI config in tool-expected locations | met | `AGENTS.md`, `.github/` committed on branch `feature/payments-submission` |
| D4 | `ai/` directory | met | plan, sessions, this file |
| D5 | Exported Acme-client sessions | met, with limits | `ai/transcripts/` holds redacted, mechanically converted transcripts of the whole build session, bootstrap and slices 1-9 plus the close-out (full prompts, answers, assistant text, tool calls and results, see `ai/transcripts/README.md`). `ai/sessions/` holds curated summaries for slices 6-9 only; slices 1-5 have no curated summary, their transcripts are the record. Limits: tool output and arguments over 4000 characters are cut, hidden reasoning and system prompts are not included, and the turn that wrote the export is not in it. The checkpoint summaries are not part of the record |
| D6 | Single GitHub repository | met, with limits | branch `feature/payments-submission` is pushed to `chadsinglee/payments-integration-service`; the remote has no `main` yet, so no pull request exists |
| D7 | 5-10 minute video | missing | owner: user |
| D8 | Personal info removed from sessions | unverifiable | records written without names or paths |

## Domain constraints
All 19 constraints in `SOLUTION.md` section 2 are met, each with code and test as listed in the review. Two have small test gaps: `acme-timing` (the pooled-connection idle timeout is a setting with no test) and `callback-auth` (constant-time comparison is not testable).

## Built beyond the brief
Redis rate limiter with a shared 429 cooldown, the half-open breaker, lease revalidation, reconciliation worker, pending expiry, `needs_investigation`, `rejected_callbacks` and `event_conflicts`, settlement cutoff and late fields, observed reversal/refund events, stub admin endpoints, `/health`. Each is recorded as a decision in `SOLUTION.md`.

## Partial, missing or deferred (proposals)
1. D6: commit and push to GitHub once the user confirms (not done by request).
2. D7: video, owned by the user.
3. D5: slices 1-5 have transcripts but no curated summary; accept as is.
4. Accept as deferred: the idle-timeout test; the Compose scenarios (slow stop, hard kill, stub callbacks) were run manually and recorded in `ai/sessions/slice-9-hardening.md`, not automated.
