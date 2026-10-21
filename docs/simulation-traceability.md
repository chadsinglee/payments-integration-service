# Simulation traceability

What the black-box simulation (`tests/Payments.CustomerSimulation`) proves about `docs/spec.md`, requirement by requirement. It complements `ai/spec-conformance.md` (which maps requirements to code and unit/integration tests) by saying what is proven **on the running stack**.

Proof layers: **Sim** = this project, against the running compose stack; **Int/Unit** = `tests/Payments.IntegrationTests` / `tests/Payments.UnitTests` (see `ai/spec-conformance.md`); **Gap** = not proven by any automated runtime check.

| ID | Requirement | Sim proof | Other proof |
|---|---|---|---|
| F1, F3 | Submit and query a payment | `PayingTests` (submit → poll → approved; unknown customer / sub-merchant 404; validation 400); `SpecComplianceTests` (fractional amount 400) | Int |
| F4 | Approved | `PayingTests`; traffic runs (70% approve, authAmount checked) | Int |
| F5 | Declined (`Success` with code ≠ 00 is a decline) | `PayingTests` (99005, 99051); traffic Decline | Unit classifier |
| F6 | Duplicate app payment id (200 same, 409 different) | `PayingTests` incl. concurrent duplicates | Int |
| F7 | Duplicate notifications | `AcmeCallbackTests` (byte-identical redeliveries); `SpecComplianceTests` (webhook twice) | Int |
| F8 | Out-of-order notifications | `AcmeCallbackTests`; `SpecComplianceTests` webhook-before-approval (refund and reversal during Acme's 35 s hold) | Unit status rules |
| F9, F10 | Reversal / partial and full refund, over-refund | `AcmeCallbackTests`; traffic Reversal, PartialRefund, FullRefund, DuplicateRefund | Unit/Int |
| F11 | Timeout/outage: never a new TransactionID, reconcile | `AcmeTroubleTests` (503, 429, outage); traffic Outage/AcmeBlip/RetryStorm (distinct TransactionIDs checked at the stub); chaos run with worker restart | Int reconciliation |
| F15 | Daily summary per sub-merchant | `DailySummaryTests`; traffic summary deltas per sub-merchant | Int (SQL) |
| F17 | Public callbacks authenticated | `SpecComplianceTests` (403 wrong merchant, 400 non-JSON, 413 over 64 KB, extra JSON fields ignored); `AcmeCallbackTests` (advice 401 on missing/wrong credentials, forged webhook signature 401, missing fields 400) | Int/Unit |
| N3 | Concurrency-safe Acme calls, no degradation | `SpecComplianceTests` burst of 150: all approved, accepted < 5 s, never more than 20 posts in any second at the stub; traffic runs | Int rate limiter |
| N4 | No secrets in logs | `SpecComplianceTests` token/email absent from responses; opt-in `SIM_DOCKER=1` scans container logs | Int |
| N6 | Runs locally in containers | every Sim test runs against `docker compose up` | smoke |
| F2 | Acme request built from sub-merchant and card (CardAcceptorName, MCC, email) | Gap: the stub keeps only transaction id, message id, order number and amount | Unit (`DomainBasicsTests`, `AcmeClientTests`) |
| F12 | Acme stub matches Appendix A | Gap: Sim relies on the stub, so it cannot prove the stub matches Appendix A | `StubTests` |
| F13-F14, F16, F18, N1-N2, N5, N7-N8, D1-D6, D8 | Design, schema, seed, docs, repo | Not runtime behaviour; seed data is implicitly exercised (cust-001/002, both sub-merchants) | `ai/spec-conformance.md` |
| D7 | 5-10 minute video | Gap: owned by the user | n/a |

## Known limits of the simulation
- **Shared understanding.** The stub is written from the same reading of Appendix A as the service. Passing proves the service behaves per that reading, not that the reading is right.
- **True client timeout (> 40 s).** The stub's slow delay (35 s) is below the client timeout and is not configurable from outside, so a real timeout is only covered by Int/Unit tests.
- **Request body fields, non-ZAR grouping, settlement cut-off / late events.** Not observable over HTTP; covered by Int/Unit tests.
- **Scale.** Runs are 60 s (default), 5 min (`SIM_MEDIUM=1`) or 60 s with a worker restart (`SIM_CHAOS=1`). The 5-minute run (1,500 payments, 5/s, single worker) reconciled with 0 mismatches across API, Acme stub and summary. Nothing proves multi-hour soak or multi-worker behaviour.

## Findings
- **Peak Acme arrival rate overshot the limiter budget (fixed: default 18 → 15).** With the limit at 18 and a burst of 150 payments, the stub saw 21-22 requests in some 1-second windows (every run). Cause, measured with the new Debug dispatch log (`ACME_GATE_LOG_LEVEL=Debug`, logs Redis's own grant time next to the worker's dispatch time): Redis never granted more than 18 in any second (the limiter is correct), but grants come in bursts (a whole window's worth at once, then again one second later) and each call leaves the worker 1-22 ms after its grant. That jitter lets the tail of one burst and the head of the next fall inside one second at Acme's side (18 granted, 22 dispatched). Any sliding-window limiter does this; the fix is headroom. At 15: Redis peak 15, dispatch peak 19, and the burst test (strict ≤ 20 per second at the stub) passed 4 of 4. A smoother that spaces grants (minimum gap of about 1000/limit ms) would remove the burstiness; not done, it is a design change (recorded in `SOLUTION.md` section 12).
- **Postgres connections can run out under a burst (fixed: default pool size of 40 per process).** Twice, the first burst of 150 concurrent submissions after a container restart got HTTP 500 (`53300: too many clients already`); `max_connections` is 100 and 93 connections were in use during a burst. Npgsql's default pool is 100 per process, so Api and Worker together could exceed it. Now excess requests wait for a connection instead.
- **Tail latency under load is head-of-line blocking by slow Acme calls (open; remedy proposed, not applied).** In the 5-minute run (1,500 payments, 5/s, seed 1116610219) Approve had p50 0.3 s but p95 5.6 s and max 14.5 s. Cause, measured from `acme_interactions`: the worker runs 4 submission loops (`Worker__Concurrency`, default 4) and each Acme call holds a loop until it returns. The `SlowAcme` mix (1%, 35 s calls) occupied 3 of the 4 loops at times (4 overlapped at least once); while 0-2 slow calls were in flight, payments waited at most 1.3 s to be picked up, with 3 in flight the p95 wait was 12.3 s and the max 14.5 s. Same seed with `Worker__Concurrency=16`: Approve p95 0.5 s, max 1.3 s, 0 mismatches. Acme can hold a request up to 30 s, so a few slow calls can stall a small fixed pool. Suggested: raise the default (loops hold no database connection while waiting on Acme, so the pool cap of 40 is not the constraint), or size it from the rate limit and expected call time (15 requests/s with 30 s calls would need hundreds in flight in the worst case, so a fixed number is only a mitigation). Decision for the owner.
- No product defects were found in the other scenarios. Failures during development of the simulation were wrong test assumptions, now fixed.
