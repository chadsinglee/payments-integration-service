# SOLUTION

> Design document required by `docs/spec.md` (Deliverables). It also carries the working sections used during the build (Decisions, Domain constraints, Pack overrides, Deliverables checklist), kept for reviewers.
> Status: built and verified (see section 11 and `ai/spec-conformance.md`).

## 1. Overview
A .NET 8 service between the Zapper app and the fictional PSP Acme Pay. The app submits a payment (customer id, sub-merchant id, amount, app payment id); the Api records it and returns 202; Worker instances submit it to Acme. Acme's Payment Advice notifications and Transaction Status webhooks (reversal/refund) update local state. A daily per-sub-merchant/currency summary supports settlement. An Acme stub (Appendix A) runs alongside in Docker Compose.

## 2. Domain constraints
No compliance regime is named. These come from the spec and override the generic packs.

| Name | Constraint | Spec reference |
|---|---|---|
| `money-minor-units` | `Amount`/`AuthAmount` are `long` minor units; never float. Webhook `transactionAmount` is a JSON number: parse as `long`, reject fractions. A partial refund's amount is the event's amount. | Conventions; webhook note |
| `currency-default-zar` | `CurrencyCode` optional; Acme applies ZAR. We always send `ZAR` (`fixed-request-values`). | Conventions |
| `no-card-data` | Store tokens only; never log tokens, API key, secrets. | POST /api/payment |
| `acme-credentials-env` | API key (`sk_test_...`), `MerchantID`, advice credentials and webhook secret come from the environment; advice `MerchantID` is checked. | Integration requirements |
| `rrn-unique` | First 12 chars of `OrderNumber` = RRN, unique per account, alphanumeric. We use exactly 12 chars so RRN = OrderNumber. | OrderNumber |
| `transaction-id-idempotency` | Acme replays the outcome for a repeated `TransactionID` for 24 h. | TransactionID |
| `timeout-no-new-transaction-id` | A timed-out request may have reached the acquirer. Never resubmit under a new `TransactionID`; never retry with a new one because reconciliation was exhausted; use RTTQ. | Timeouts |
| `message-id-unique` | `MessageID` is unique per message (resends get a new one, same `TransactionID`); Acme's response `MessageID` is stored in the audit table. | Conventions |
| `acme-timing` | Usually <400 ms, up to 30 s; idle connections closed after 60 s. | Timeouts |
| `acme-rate-limit` | 20 req/s for POST and RTTQ combined; 429 carries `Retry-After`; 5xx = degraded gateway (outcome unknown). | Errors |
| `status-vs-decline` | `TransactionStatus=Success` with code != `00` from the acquirer is a decline. Advice `TransactionStatus` has its own values (Success/Failure/Other). RTTQ `Processing` is non-final. | Transaction status |
| `callback-idempotent-unordered` | Callbacks repeat and arrive out of order without duplicate or conflicting records; reversals/refunds can arrive days later, correlated by `rrn`. | Task 1 |
| `ack-after-durable-write` | Answer 200 only after the event is committed. Acme resends only 3 times, so RTTQ reconciliation is the only recovery for lost advices. | Notifications |
| `callback-auth` | Advice: HTTP Basic. Webhook: `x-signature` = Base64 HMAC-SHA256 of the exact raw body bytes, constant-time compare, verified before parsing. Endpoints are public. | Scope; Appendix A |
| `tolerant-reader` | Ignore unknown fields in Acme responses/notifications. | Conventions |
| `facilitator-fields` | `SubMerchantID` <=15, MCC exactly 4, `CardAcceptorName` exactly 40 chars space-padded: `ZAP*` + sub-merchant name in positions 1-23 (`*` at position 4, FNB), city 24-36 (<=13), region first 2 chars 37-38, country 39-40. Other limits: TransactionID/OrderNumber <=50, Email <=254. | Payment facilitators |
| `timestamps-sast` | Acme timestamps are SAST (UTC+2), no offset, ms precision; convert explicitly, store `timestamptz` UTC; SAST day computed with `AT TIME ZONE 'Africa/Johannesburg'`, never session time zone. | Conventions |
| `env-config` | All configuration from the environment; no secrets in the repo. | Integration requirements |
| `no-lost-audit` | Payments, lifecycle changes, notifications and Acme interactions are append-only; nothing hard-deleted. | Task 2 |

## 3. Decisions
| Name | Choice | Reason | Rejected |
|---|---|---|---|
| `database-engine` | PostgreSQL 16 in Compose; RDS/Aurora PostgreSQL on AWS | Unique constraints, `ON CONFLICT`, `FOR UPDATE SKIP LOCKED`; Postgres pack exists | SQL Server; SQLite |
| `data-access-and-migrations` | Dapper + versioned `.sql` migration scripts | Schema visible (assessed in Task 2) | EF Core migrations |
| `layering-style` | Clean layered projects: Domain, Application, Infrastructure, Api, Worker, Acme.Stub + UnitTests + IntegrationTests | Compiler-enforced dependency direction | Single project; vertical slices |
| `submit-flow` | Async accept: Api inserts `Pending` row, returns 202; Worker submits; app polls GET. Work stays in Postgres (`postgres-outbox-with-redis-limiter`) | App never waits on Acme's 30 s tail; no dual-write | Sync wait; hybrid; SQS dispatch (extra infra and scope) |
| `worker-lease-claims` | Claim = short transaction: set `Submitted`, `lease_until = now + Acme timeout + margin`, `attempt_count`, `next_attempt_at`, `first_sent_at`, commit, then call Acme. **No row lock is ever held during an HTTP call.** Expired lease on `Submitted` = outcome unknown, resolved by RTTQ, never by a blind resend. Claims use `FOR UPDATE SKIP LOCKED`. | Callbacks never block on a slow Acme call; crashes are recoverable | Hold the lock during the call; commit-and-forget |
| `reconciliation-worker` | Separate Worker container (same codebase, Worker host): sends `Pending`, resolves expired leases and `Unknown` via RTTQ; bounded parallelism per instance; multiple replicas allowed | Independent scaling and shutdown | In-process BackgroundService |
| `acme-client-resilience` | Typed `HttpClient` with a timeout-only resilience handler (no 10 s per-attempt timeout; timeout above 30 s; pool idle < 60 s; no automatic POST retry). A 429 releases the claim (`Submitted` back to `Pending`, attempt not counted) with `next_attempt_at = now + Retry-After`; the next claim builds a new `MessageID` (same `TransactionID`). The circuit breaker is the in-process `AcmeGate` (not a Polly handler, so it can stop claiming rather than fail calls): five consecutive 5xx/timeouts open it for 30 s; while open nothing is claimed, so no attempt is counted; after it closes exactly one probe call is admitted (half-open): its success closes the breaker, its failure re-opens it, and other claims wait (a denied or abandoned probe frees the slot, with a 2-minute backstop). RTTQ is not retried by the client: an unusable answer is a reconciliation back-off (`reconciliation-worker`). | Meets concurrency/degradation requirement and `timeout-no-new-transaction-id` | Polly breaker handler; minimal timeout+semaphore |
| `global-rate-limit` | Redis sliding one-second window (atomic Lua script, Redis `TIME`) shared by all Worker instances for POST and RTTQ together, default 15 per second, configurable from 1 to 19 (20 or more is rejected at startup, because Acme rejects above 20). A 429 from Acme also publishes a shared cooldown key in Redis (`acme:cooldown`, only ever lengthened), checked in the same Lua script, so every worker stops calling for the `Retry-After` period, not just the one that was throttled. A sliding window rather than a token bucket, because a bucket's burst plus refill can exceed the limit inside one second. If Redis is unavailable (or the call fails) workers pause outbound calls for 2 s and retry; nothing is claimed while paused, and payment state and queued work stay in Postgres. A worker claims first and then takes a token (so idle polling spends no budget); if the token cannot be had within 3 s the claim is released without counting an attempt. | Scales workers independently while staying under Acme's limit | Single replica; static per-replica share; DB-backed bucket; token bucket |
| `shared-outcome-path` | One Application use case applies every outcome (Api callbacks, Worker POST/RTTQ results, lease recovery): take the payment row lock, write a `payment_event`, recompute status from all events, commit (all in one transaction, no HTTP inside). A move to `Unknown` applies only if the payment is still `Submitted`. | A callback that arrives before the worker's response can never be overwritten | Separate update paths |
| `notification-ordering-and-dedup` | `payment_events` with unique `(source, dedup_key)` plus a payload hash. Dedup key: webhook `transactionUUID`; advice `(TransactionID, RequestType, TransactionStatus, ResponseCode)`. Same key with a different hash is written to the conflict record (flagged and logged), never silently dropped. Unknown `rrn`/`TransactionID`: stored as unmatched, logged, 200. A webhook that arrived before its payment is retried by the reconciliation sweep (`rrn` → `order_number`, backoff between tries) and, once matched, adopted through the shared outcome path (same currency/amount checks, dedup and `Recompute` under the row lock), so it applies exactly once; it is counted on its own SAST day after adoption. The retry stops after `Reconciliation__UnmatchedMaxAttempts` (default 12) or `Reconciliation__UnmatchedMaxAgeMinutes` (default 60): `unmatched_event_sweeps.exhausted_at` is set and an `ALERT` is logged (there is no payment to flag). Advice is not swept (its `TransactionID` is ours, so it cannot precede the payment). Migration `V1_06` lets `payment_events` change only in that one way (null `payment_id` gains its id, type may drop to `Informational`; see §7); everything else stays append-only. `isApproved=false` or `txResultCode!=00`: recorded, not applied. | Constraints give dedup, rules give order independence | Derived-only status; parking/replay |
| `conflicting-outcomes` | Acquirer evidence outranks any local inference. Two conflicting acquirer outcomes: first wins, later stored and flagged, alert raised. | Never hide a real charge | Latest timestamp wins |
| `unresolved-and-reconciliation-state` | Payment outcome is kept separate from `reconciliation_state` (`None`, `InProgress`, `Exhausted`). If the outcome stays unknown after a timeout, it remains `Unknown`; when automated recovery is exhausted (resend window, RTTQ attempts) the payment is flagged `needs_investigation` and its unresolved state is exposed to the app. It is never marked definitively failed, and valid late acquirer evidence is still processed. No new `TransactionID` is ever used. | User choice; fits `timeout-no-new-transaction-id` | A separate `Expired`/`Failed` for inferred failures |
| `rttq-404` | RTTQ 404: resend with the same `TransactionID` within `resend-window` (default 10 minutes from `first_sent_at`, configurable, new `MessageID`). The first resend is immediate; later ones wait `Backoff(attempts - 1)` and stop at `MaxAttempts`. After the window or the attempts, `reconciliation_state = Exhausted` |
| `pending-expiry` | A payment never sent after N minutes (worker down) becomes `Failed` (certain: Acme never saw it), overridable only if evidence shows otherwise. The sweep re-checks `Pending` under the row lock, so a payment a worker claimed meanwhile is never failed. Payments released by a 401, 429 or missing rate budget carry a `deferred_at` marker and are never expired (held back by us, not abandoned), and the sweep does not run while the Acme gate is paused. | Pending cannot hang forever | Leave Pending |
| `rttq-reversed-refunded-complete` | RTTQ `Reversed`/`Refunded` is stored as an observed event (`ReversalObserved`/`RefundObserved`): it moves the displayed status and is flagged until the webhook (`transactionUUID`) arrives, but creates no financial amount. A reversal, observed or real, is never undone by a later approval advice. RTTQ `Refunded` also proves an earlier approval. `Complete` counts as approved only when the response code confirms approval. | No invented amounts, no double-counting, reversals preserved | Synthetic facts |
| `rejected-callbacks` | An authenticated advice/webhook that fails validation (bad shape 400, other merchant 403) is stored once in append-only `rejected_callbacks` (bounded body, deduplicated by payload hash) and logged at Warning with source, reason, hash and length only, never the body. The response stays 400/403, so Acme's three resends are harmless duplicates and RTTQ reconciliation is the recovery. A storage failure returns 500. Unauthenticated calls are never stored. | Nothing is lost, no log leakage, behaviour consistent with Acme's retry | Return 200; log the body |
| `auth-amount-source` | When approval is known only from an advice, the worker calls RTTQ to get `AuthAmount` | Accurate partial authorisations | Advice `Amount` |
| `daily-summary-semantics` | SAST calendar day. One outcome fact per payment: its earliest Approved event, or, if it never had one, its earliest Declined event (Approved beats Declined, as in the status rules, so a payment is never both; declines use the requested `Amount`). Reversals and refunds count per webhook `transactionUUID` only when `isApproved && txResultCode=00` (other notifications are stored as `Informational`), including ones our status rules flag rather than apply (over-refund, refund after reversal): Acme reports that money as moved. POST-response and RTTQ events carry Acme's `TransactionDateTime` as `occurred_at`, so a timeout resolved next day still lands on its own day. Known limits: the approved amount can still change after the cutoff when RTTQ supplies `AuthAmount` (not flagged late); the `V1_05` index should be built `CONCURRENTLY` on a live production table. Gross (a later-reversed approval still counts as approved on its day). Approved uses `AuthAmount`, the requested amount only until RTTQ supplies it. RTTQ-observed reversals/refunds and unmatched events are not counted (no `transactionUUID` / no sub-merchant). One committed query, `sql/daily-summary.sql` (positional parameters: day, cutoff), is embedded in the Infrastructure assembly and run by `GET /summary?date=`, and can be run directly in psql; an expression index on the event day keeps it off a full scan. | Each payment counted once; reversals/refunds visible | Net per payment; UTC created-day |
| `settlement-cutoff` | A SAST day is final `Summary__CutoffHours` (default 12) after it ends. Events received after that still count on their own day, and the response reports them in `lateCount`/`lateAmountMinor` per metric, with `cutoffAt` and `settled` per request | Totals are stable once settled while staying accurate | Silent restatement; received-time day |
| `declined-definition` | Declined = outcome from the acquirer (`ResponseSource=Acquirer` or non-`E` code) with code != `00`; gateway errors (E01/E02/E09/E50) are never declines | Matches the spec | Count gateway errors |
| `duplicate-payment-id` | Unique on app payment id. Same payload: return the original, no resend. Different payload: 409. | Idempotent and safe | Always return original |
| `multiple-cards` | Stored card has a default flag; the default card is used | Deterministic; request unchanged | One card per customer; card id in request |
| `partial-auth-and-refunds` | Summary uses `AuthAmount`; refunds accumulate vs `AuthAmount`; `PartiallyRefunded` until fully refunded; over-refund stored and flagged, not applied | Money-accurate | Single refund on requested amount |
| `advice-request-types` | `Authorize`/`Capture`/`Payment` all apply by `TransactionStatus` + `ResponseCode`; `Other` stored, status unchanged | Simple; nothing lost | Payment only |
| `fixed-request-values` | `Channel=App`, `PaymentService=CardNotPresent`, `CardOnFileScenario=CIT`, `CurrencyCode=ZAR`, facilitator name `ZAP` | Implied by the spec examples | n/a |
| `order-number-scheme` | We generate `OrderNumber` as `Z` + 11 digits from a DB sequence (exactly 12 chars) and a GUID `TransactionID` once at insert; unique index on `order_number` (the RRN) | Unique by construction; app id format unknown | Hash of app id |
| `stub-scenario-control` | Stub picks behaviour from magic values (token/amount: approve, decline 05/51, 35 s delay, 429, 5xx, duplicate and out-of-order callbacks) plus an admin endpoint to emit reversal/refund | Cheap, demoable | Config-file driven |
| `app-facing-contract` | Payment status values per section 3.1 plus a `needsInvestigation` flag. POST: 202 new, 200 same-payload repeat, 409 conflict, 404 unknown customer/sub-merchant/default card, 400 invalid. RFC 7807 `ProblemDetails`. | Predictable for the app | n/a |
| `acme-401` | Acme 401/E02 (POST or RTTQ): the call processed nothing, so the claim is released (payment back to `Pending`, or the RTTQ attempt given back), the worker pauses 60 s (`AcmeGate`, trips the breaker so one further 401 re-pauses at once) and an `ALERT` is logged; the API key is never logged | One config mistake does not fail every payment | Fail the payment |
| `lease-revalidation` | After the limiter grants a token and immediately before the POST (or RTTQ query), the worker renews its lease with a conditional update (`status=Submitted`, same `attempt_count`, `lease_until > now()`). If no row changes, the lease expired or another worker took over: it makes no call, logs a warning and leaves the payment to lease recovery (RTTQ), never a blind resend. Recovery only scans `lease_until < now()` and renewal requires `lease_until > now()`, so the two cannot both win. | A slow limiter wait can never produce a POST from a worker that no longer owns the payment | Longer fixed lease only; check without renewing |
| `graceful-shutdown` | The worker host waits `Acme timeout + 15 s` on SIGTERM (`HostOptions.ShutdownTimeout`) and stops both workers concurrently (`ServicesStopConcurrently`), so neither starts new calls while the other drains; Compose `stop_grace_period` is 60 s (ECS `stopTimeout` must exceed it too). Loops stop claiming at once; an Acme call already in flight finishes and is recorded (persistence uses no cancellation token). A worker cancelled while waiting for the limiter releases its claim without counting an attempt. A hard kill is recovered through lease expiry and RTTQ. | No lost outcomes on deploy | Cancel in-flight calls |
| `secret-redaction` | Secrets (API key, card token, email, connection strings, webhook secret) are never logged: records that carry them override `ToString`, `Authorization` is redacted in HTTP client logs, rejected callbacks log source, reason, hash and length only, and errors log exceptions without request data. Every log line while processing a payment carries `PaymentId` and `TransactionId` as a scope (console `IncludeScopes`). | Local troubleshooting without leaking | Mask at the sink |

No `OPEN` decisions.

### 3.1 Status rules
Stored status is recomputed from all of a payment's events under the row lock (order independent), not moved step by step.

| Rule | Behaviour |
|---|---|
| Statuses | `Pending`, `Submitted`, `Unknown`, `Approved`, `Declined`, `Failed`, `Reversed`, `PartiallyRefunded`, `Refunded` |
| Base outcome | From acquirer evidence only (POST response, advice, RTTQ) |
| Reversed | An applied or RTTQ-observed reversal exists and the base outcome is approved or not yet known; later approvals do not undo it |
| Refunded / PartiallyRefunded | Sum of applied refunds compared with `AuthAmount` (not `Amount`) |
| Refund before approval | Stored; applied once `AuthAmount` is known |
| Over-refund, refund after reversal, reversal or refund on a declined/failed payment | Stored and flagged, not applied |
| `Failed` | Only certain non-processing (never sent, 400/E01, foreign 409/E09) |
| Timeout, 5xx, connection error | Outcome unknown: `Unknown` then RTTQ, never `Failed` |
| 409/E09 with our own `TransactionID` | Acme already has it: RTTQ |
| 401/E02 | `acme-401` |

### 3.2 Acme response mapping
| Acme result | Our status/action |
|---|---|
| 200, `Success`, code `00` | `Approved` (store `AuthAmount`) |
| 200, `Success`, code `COMPL` from source `EFT` | `Approved` (EFT collection completed; not expected for our card-not-present payments) |
| 200, code `PEND`, or `COMPL` from a non-EFT source | Not final: stays `Unknown`, RTTQ keeps asking (advice: stored, no status change) |
| 200, `Success`, other code != `00` from acquirer | `Declined` |
| 200, `Failure` from Application source (E-codes) | `Failed` |
| 400/E01 | `Failed` + alert (bug on our side) |
| 401/E02 | `acme-401` |
| 409/E09, foreign `TransactionID` | `Failed` + error log |
| 409/E09, our `TransactionID` | RTTQ |
| 429/E29 | Reschedule using `Retry-After` |
| 5xx/E50, timeout, connection error | `Unknown` then RTTQ |
| RTTQ `Processing` | Stay `Unknown`, back off |
| RTTQ 404 | `rttq-404` |

Webhook checks: `transactionAmount` must be a whole non-negative number (`1999` and `1999.0` are accepted, `19.99` is rejected with 400 and stored as a rejected callback). An approved reversal or refund whose `currencyCode` differs from the payment's, or whose amount exceeds the payment (the approved amount once known), is stored as `Informational`, never applied, and flags the payment for investigation with an ALERT log.
## 4. Pack overrides
| Pack rule | What this repo does instead | Spec reference |
|---|---|---|
| `dotnet-api`: auth attribute on every action | No caller auth on the app API; callbacks use Basic auth / HMAC | Scope |
| `messaging-services`/`dotnet`: only bootstrap values come from env | All configuration from environment | Integration requirements |
| `database-postgres`: Flyway `Vx_y__` naming, reader/writer split, time partitioning, hot/cold tiering, generated BIGINT ids, status as small ints | Plain versioned SQL scripts, single connection string, no partitioning/tiering (noted in section 7), `bigint` money, sequence ids, status text with CHECK | Task 2 scope; ~4 h guideline |
| Packs reference `docs/ARCHITECTURE.md` | Design doc is `SOLUTION.md` | Deliverables |
| Plans/conformance under `docs/` | Under `ai/` | AI artifacts |
| Template README | Replaced by run-instructions README | Deliverables |
| `dotnet`: SDK | Pin `net8.0` via `global.json` | Technology |
| Pack no-network-in-transaction rule | Applies: the shared outcome path and claims never call Acme inside a transaction | Postgres pack |

## 5. Shape (projects and dependency direction)
`Payments.Domain` (status rules, money, `CardAcceptorName` formatter; no deps) <- `Payments.Application` (use cases incl. `shared-outcome-path`, ports) <- `Payments.Infrastructure` (Postgres/Dapper, migrations, Acme client, `RedisAcmeRateLimiter`; Application holds `PaymentSubmissionService`, `ReconciliationService` and the `AcmeGate` breaker/pause gate; Worker hosts the submission and reconciliation workers) <- `Payments.Api` and `Payments.Worker` (composition roots). `Acme.Stub` is standalone. Tests: `tests/Payments.UnitTests` and `tests/Payments.IntegrationTests` (Testcontainers Postgres and Redis; tagged integration). Where new code goes: rules in Domain, orchestration in Application, I/O in Infrastructure, HTTP/hosting only in Api/Worker.

## 6. Diagram
```text
 Zapper app
   | POST /payments (202) | GET /payments/{id} | GET /summary?date   [app-facing-contract]
   v
+---------------------+   Acme callbacks (public)        +----------------------------+
|    Payments.Api     |<---------------------------------| Acme Pay  (Acme.Stub)      |
| - app endpoints     |   Payment Advice (Basic auth)    |  POST /api/payment         |
| - advice endpoint   |   Status webhook (HMAC raw body) |  GET  /apirttq/query/...   |
| - webhook endpoint  |   [callback-auth]                |  magic values + admin      |
+----------+----------+                                  |  [stub-scenario-control]   |
           |                                             +-------------^--------------+
           v                                                           |
+-----------------------------------+                                  |
| Payments.Application              |   Payments.Worker (N replicas) --+
|  shared-outcome-path (1 tx:       |   [reconciliation-worker]  POST payment / RTTQ
|  lock row -> event -> recompute)  |   claim w/ lease [worker-lease-claims]
|  uses Payments.Domain             |   typed client [acme-client-resilience]
|  (status rules 3.1, money,        |        |
|   CardAcceptorName)               |        v
|  [layering-style]                 |   Redis sliding window <=19 rps (default 18), POST+RTTQ
+----------------+------------------+   plus shared 429 cooldown [global-rate-limit]; down -> pause outbound
                 ^
                 | ports
+----------------+------------------+
| Payments.Infrastructure           |----> PostgreSQL 16  [database-engine, data-access-and-migrations]
| Dapper repos, SQL migrations,     |      sub_merchants, stored_cards, payments,
| Acme client, Redis limiter        |      payment_events, acme_interactions
+-----------------------------------+

Flow A (submit)  [submit-flow]: Api -> look up customer / sub-merchant / default card ->
  insert Pending (unique app id; OrderNumber, TransactionID) -> 202.
  Worker: claim (lease) -> take limiter token (release on denial) -> renew lease (abort if lost)
  -> POST -> shared-outcome-path.
  timeout/5xx/expired lease -> Unknown -> RTTQ (same TransactionID; 404 -> resend inside
  resend-window; exhausted -> needs_investigation) [rttq-404, unresolved-and-reconciliation-state].
Flow B (callbacks)  [notification-ordering-and-dedup]: Acme -> Api: verify auth on raw bytes ->
  shared-outcome-path (dedup key, recompute status) -> 200 after commit.
Summary [daily-summary-semantics, settlement-cutoff]: SQL over payment facts + events, SAST day.
```

## 7. Data model, trade-offs, evolution
**Tables** (versioned scripts `src/Payments.Infrastructure/Schema/v1/V1_00`-`V1_06`, applied at Api startup):
- `sub_merchants`, `stored_cards` (token, default flag; customers are referenced by id): seed data only.
- `payments`: one row per app payment id (unique); `order_number` (the 12-character RRN, unique), `transaction_id` (GUID, fixed for life), status, `reconciliation_state`, `needs_investigation`, lease columns (`lease_until`, `attempt_count`, `next_attempt_at`, `first_sent_at`), `deferred_at`, `auth_amount`.
- `payment_events`: append-only facts (POST results, RTTQ results, advices, webhooks) with a unique `(source, dedup_key)` and a payload hash. Status and the summary are derived from these. Append-only is enforced by a trigger with one exception (`V1_06`): a row stored with a null `payment_id` (a webhook that arrived before its payment) may gain its `payment_id`, and its `event_type` may change to `Informational` (late event failed validation). The trigger compares every other column as one value, `to_jsonb(NEW) - 'payment_id' - 'event_type'` against the same for `OLD`, so a column added later is protected without editing the trigger (covered by a test that adds a temporary column). Re-pointing, clearing `payment_id`, other type changes, DELETE and TRUNCATE are rejected.
- `unmatched_event_sweeps`: mutable bookkeeping for those events (attempts, `last_attempt_at`, `exhausted_at`), kept out of the append-only table. It has no foreign key to `payment_events`, because an FK would turn the `TRUNCATE` guard's error into a different one.
- `event_conflicts`: same dedup key with a different hash, or conflicting acquirer outcomes.
- `rejected_callbacks`: authenticated callbacks that failed validation (bounded body, deduplicated by hash).
- `acme_interactions`: request/response audit of every Acme call; the request copy has the card token and customer email redacted.

**Trade-offs.**
- Postgres is both store and queue (rows with leases and `SKIP LOCKED`), so there is no dual write and no extra infrastructure. Cost: polling, and throughput bounded by Postgres, which is ample at Acme's 20 calls per second.
- Status is recomputed from events under a row lock: order independent and auditable, at the cost of reading a payment's events on every update (a handful of rows).
- Redis holds only the rate window and the 429 cooldown, so losing it pauses outbound calls rather than losing data. The limiter is a sliding window (strict) rather than a token bucket (burstable past the limit).
- No automatic POST retry: an uncertain outcome becomes `Unknown` and is resolved through RTTQ, trading latency for never double charging.
- Dapper and plain SQL keep the schema and the summary query visible and reviewable.

**Evolution.** Add columns with new `V1_nn`/`V2_nn` scripts (additive first, then backfill, then constrain); build indexes `CONCURRENTLY` on live tables. Partition `payment_events` and `acme_interactions` by month and archive old partitions. Move the summary to a reader replica or a materialised table when volume requires. Replace polling with SQS dispatch (events written by the outcome path) if latency matters, keeping Postgres as the source of truth.

## 8. AWS deployment approach
- **Compute:** ECS Fargate: Api service behind an ALB (public callbacks and app traffic; Acme callbacks restricted by Basic auth and HMAC, optionally by WAF), Worker service with no inbound traffic. The same images as Compose.
- **Data:** RDS PostgreSQL (Multi-AZ, automated backups, point-in-time recovery); ElastiCache Redis for the shared limiter (a small replicated node; the service tolerates its loss).
- **Config and secrets:** Secrets Manager or SSM Parameter Store injected as environment variables into the task definitions (Acme API key, advice credentials, webhook secret, connection strings). Nothing is baked into images.
- **Scaling:** Api on CPU/request count; Worker on the age of the oldest `Pending` payment. Adding workers never exceeds Acme's limit because the budget (default 15 per second across all workers) lives in Redis.
- **Graceful shutdown:** on a scale-in or deploy ECS sends SIGTERM; the worker stops claiming, lets an in-flight Acme call finish and records it. Set the container `stopTimeout` (max 120 s) above the worker's `Acme timeout + 15 s` host timeout (55 s by default); the Compose equivalent is `stop_grace_period: 60s`. Anything cut off by a hard kill is recovered by lease expiry and RTTQ, never resent blindly. Deploy rolling, with new tasks healthy before old ones drain.
- **Signals:** structured console logs to CloudWatch with `PaymentId`/`TransactionId` scopes. Metrics and alarms: oldest pending age, Acme latency, 429 and 5xx rates, breaker and limiter pauses, 401 alerts, callback rejections, unmatched and conflicting events, `needs_investigation` count, RDS CPU/connections and Redis availability. Alarm on the `ALERT` log lines (401, conflicting outcomes).
- **Network:** private subnets for Worker, RDS and Redis; egress to Acme through a NAT gateway (a fixed IP Acme can allow-list); security groups per tier.

## 9. Assumptions and Acme stub behaviours
**Stub behaviours** (`src/Acme.Stub`, `stub-scenario-control`; amounts in cents, all otherwise approve): `99005` decline 05; `99051` decline 51; `99422` application failure; `99409` foreign RRN in the response; `99503` gateway error 503 (outcome `Unknown`); `99502` fails the first POST per `TransactionID` (RTTQ 404, then resend); `99429` 429 with `Retry-After: 1` on the first POST per `TransactionID`; `99035` answers after 35 s (client timeout, then RTTQ finds the approval). Admin: `POST /admin/rttq-processing/{transactionId}?count=n`, `POST /admin/outcomes/{transactionId}/status?value=Reversed|Refunded|Complete`, `GET /admin/posts`.

**Assumptions.**
- The spec's `CardAcceptorName` example is not 40 characters; treated as transcription whitespace loss, so the formatter pads to 40.
- `Complete` counts as approved only with an approving response code.
- Payment amounts are integer minor units (cents) in ZAR.
- Customers are identified by id and have a default stored card; the app sends no card details.
- The app payment id format is unknown, so we generate our own 12-character `OrderNumber` and a GUID `TransactionID`.
- Advice requests use HTTP Basic auth and webhooks an HMAC-SHA256 over the raw body in `x-signature` (the spec leaves the exact scheme open); credentials are configuration.
- The summary's SAST day, 12-hour settlement cutoff and gross figures are our reading of "daily summary" (see `daily-summary-semantics`, `settlement-cutoff`).
- Resend window 10 minutes and pending expiry 15 minutes are defaults, configurable.
- Deferred: no end-user authentication or TLS (the ALB terminates TLS on AWS); no metrics exporter (log-based diagnostics only).
- Stub callbacks: `POST /admin/callbacks/{transactionId}?send=advice,refund:3000,...` makes the stub send real, authenticated advices and webhooks to the Api in the given order (repeats are byte-identical duplicates).

## 10. AI tools, workflow and validation
**Tools and context.** GitHub Copilot CLI (Claude models) with the repo's AI starter template: `AGENTS.md`, instruction packs in `.github/instructions/`, skills (`bootstrap-repo`, `scaffold-from-spec`) and a `planner` and `code-reviewer` agent. The spec (`docs/spec.md`) was the only requirements source; every decision is named in section 3.

**Workflow.** Bootstrap produced the decisions and diagram (critiqued by the planner agent), then a nine-slice plan (`ai/implementation-plan.md`) was approved. Each slice was built vertically (foundation and submit-and-approve first), gated by build, tests and `dotnet format --verify-no-changes`, independently reviewed by a fresh-context reviewer, then approved by me before the next. Full redacted transcripts of the session are in `ai/transcripts/`, per-slice summaries with review tables for slices 6-9 in `ai/sessions/`; the final planner conformance table is `ai/spec-conformance.md`.

**Delegated vs my own.** The assistant wrote code, tests and docs; I made the product decisions (no blind resend, unresolved handling, rate limit, resend window, deferred items) and approved each slice. Constraints such as never retrying a POST and never changing a `TransactionID` came from the spec and were enforced by tests.

**Validation.** Unit and integration tests (Testcontainers Postgres and Redis), a DI smoke test of the worker graph, a Docker Compose smoke test (approve, decline, 429 retry, summary, secret scan of logs, graceful stop; stub-emitted duplicate, out-of-order, refund and reversal callbacks; stop during a 35 s in-flight call; hard kill recovered through lease expiry and RTTQ with no second POST) and independent review every slice. Reviews caught real defects that I would not have accepted: a payment counted as both approved and declined, undated events landing on the wrong summary day, a worker able to POST after its lease expired, and a Dockerfile missing `sql/` (found only by the compose build).

**What I would do differently.** Run the compose smoke test every slice rather than at the end, and write the lease-ownership tests when leases were introduced.

## 11. Deliverables checklist
- [x] All source code: `src/` projects per section 5
- [x] App endpoints: POST payment, GET payment
- [x] Callback endpoints: Payment Advice, Transaction Status webhook
- [x] Acme stub per Appendix A: `src/Acme.Stub`
- [x] Dockerfile(s) + `docker-compose.yml` (Api, Worker, Postgres, Redis, Stub)
- [x] Environment-based config, documented in `README.md`
- [x] SQL schema + migrations; seed data (few sub-merchants, cards, payments)
- [x] Daily summary endpoint + committed `.sql` query
- [x] Tests demonstrating: approved, declined, duplicate app id, duplicate notification, out-of-order, reversal after approval, refund after approval, Acme timeout
- [x] Extra tests: callback before worker response; expired-lease recovery; 429 and slow responses do not degrade the service
- [x] Concurrency-safe Acme client; local logging/diagnostics
- [x] Unit + integration tests (`tests/`)
- [x] `README.md`: local run instructions
- [x] `SOLUTION.md` sections 7-10 complete
- [x] AI config in tool-expected locations (`AGENTS.md`, `.github/`) present and committed (branch `feature/payments-submission`)
- [x] `ai/` directory: plans, redacted session transcripts (`ai/transcripts/`), slice 6-9 summaries and `ai/spec-conformance.md`
- [ ] 5-10 minute video walkthrough (architecture, key decisions/trade-offs, implementation highlights) - **owner: user, not producible by the agent**

## 12. Open questions
None blocking. Possible improvement to consider later:

- **Paced dispatch.** The Redis limiter is a sliding window, so it grants a window's worth of tokens at once and then again a second later; calls then leave the worker 1-22 ms after their grant. Seen from Acme, the tail of one burst and the head of the next can fall in the same second (with a limit of 18, up to 22 arrived in one second; see section 13). The default of 15 absorbs this. Spacing grants evenly (a minimum gap of about 1000/limit ms enforced in the same Lua script, or one dispatcher sending at a fixed rate) would remove the burstiness and allow a higher limit. Not done: it adds latency for single payments and changes the limiter's contract. Debug logging of Redis grant time against dispatch time (`ACME_GATE_LOG_LEVEL=Debug`) shows whether it is needed.

## 13. Fixes after delivery
Defects found after the slices were approved. Each has a regression test.

| Date | Defect | Cause | Fix | Test |
|---|---|---|---|---|
| 2026-10-21 | A payment Acme never records (stub `99503`: every POST gets a 503 and every RTTQ a 404) was resent about twice a second for the whole 10-minute window, over 1,200 POSTs and RTTQ queries for one payment, flooding the worker and the logs | The resend branch of `HandleNotFoundAsync` (`ReconciliationService`) set `NextAttemptAt = now` and ignored `MaxAttempts`, bypassing the backoff and cap that `Settle` applies to every other RTTQ outcome (`rttq-404`) | First resend still immediate; later resends wait `Backoff(attempts - 1)` (10 s, 20 s, 40 s, ... capped at 5 min) and stop flagged `Exhausted` at `MaxAttempts`. About 7 attempts fit in the window. Same `TransactionID`, never a new one | `Reconcile_WhenTheResendIsNotSeenEither_DelaysTheNextResendInsteadOfLooping`, `Reconcile_WhenTheMaxAttemptsAreUsedInsideTheWindow_StopsFlaggedAndStaysUnknown` |
| 2026-10-21 | The worker log was dominated by four framework lines per Acme call | `System.Net.Http.HttpClient` logs request start and end at Information | `System.Net.Http.HttpClient` set to `Warning` in the worker's `appsettings.json`; the application's own submit, reconcile and `ALERT` lines are unchanged | Configuration only |
| 2026-10-21 | A burst of 150 payments let up to 22 POSTs reach Acme in one second (limit 20) | Redis never granted more than 18 per second, but grants come in bursts and calls leave the worker 1-22 ms after their grant, so two bursts can overlap inside one second at Acme | Default `Acme__RateLimitPerSecond` lowered from 18 to 15 (observed peak 19). Debug log of Redis grant time against dispatch time added (`ACME_GATE_LOG_LEVEL=Debug`). Paced dispatch is recorded in section 12 | `Burst_WhenOneHundredFiftyPaymentsArriveAtOnce_AcmeNeverSeesMoreThanTwentyPostsInASecond` (simulation, against the running stack), `Read_WhenNothingIsConfigured_DefaultsTo15` |
| 2026-10-21 | The first burst of 150 concurrent submissions after a restart returned HTTP 500 (`53300 too many clients already`) | Npgsql allows 100 pooled connections per process by default; Api and Worker together could exceed Postgres' `max_connections` of 100 | Default `Maximum Pool Size` of 40 per process unless the connection string sets one, so excess requests wait for a connection | `FromConfiguration_WhenNoPoolSizeIsGiven_CapsThePoolBelowPostgresMaxConnections`, `FromConfiguration_WhenThePoolSizeIsGiven_KeepsIt`, and the burst simulation |
