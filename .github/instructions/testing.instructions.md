---
applyTo: "**/*Test*/**,**/*.test.*,**/*.spec.*,**/tests/**,**/test/**"
---

# Testing Guidelines
> Stack for this repo: xUnit + Shouldly, Testcontainers (Postgres, Redis). Domain constraints in `SOLUTION.md` decide what must be tested.

## Naming
- `Method_WhenCondition_ExpectedResult` (or the language-idiomatic equivalent) — the name states the behaviour.

## Focus
- One observable behaviour per test: return value, external call, persisted state, emitted event, or a meaningful log.
- Don't assert implementation details. Mock only true external boundaries (network, DB, queues, clock).
- Separate tests when paths or outcomes differ; combine negative cases only when the observable outcome is identical.

## Structure
- Arrange / Act / Assert with a **single Act**.
- Shared setup builds the system-under-test with *plumbing-only* defaults. Business-relevant values are set explicitly in each test.
- Use factory/builder helpers with optional named parameters; pass only what the scenario cares about.
- Use distinct, traceable values per scenario. Generate IDs via the project's ID helper rather than magic constants.

## Assertions
- Use Shouldly only (no `Assert.*`, no FluentAssertions). Use xUnit `[Fact]`/`[Theory]`.
- Async assertions must be awaited and the test must return a Task/Promise — otherwise the assertion silently never runs.

## Categories
- Unit tests live in `tests/Payments.UnitTests`: no external infra, no Docker, run on every build.
- Integration tests live in the separate `tests/Payments.IntegrationTests` project (Testcontainers Postgres/Redis, stub via `WebApplicationFactory` or container). Run them explicitly (see `AGENTS.md`); the unit project never references them.
- Integration tests use real dependencies for the thing under test (don't mock the client you're verifying); share container/host plumbing via a base class — don't duplicate it.

## Isolation & concurrency
- Tests that share a database, static/global state (e.g. a process-wide meter) or cluster-wide DDL must isolate per test (unique keys/dates) or run serially.
- Code that depends on locks, unique constraints or conditional updates needs a concurrency test; unit tests don't catch those races.

## Payment-domain scenarios (required, from `SOLUTION.md`)
- Status rules (3.1): table-driven unit tests over event orderings (reversal/refund before approval, refund after reversal, over-refund, duplicate events), asserting the recomputed status is order independent.
- Money: `long` minor units, partial authorisation and cumulative partial refunds.
- `CardAcceptorName` formatter (exact 40 chars, `*` at position 4, truncation) and SAST/UTC conversion, including the day boundary at 22:00 UTC.
- Webhook HMAC and advice Basic-auth verification on raw bytes (use the spec's worked example for the signature).
- Integration (real Postgres): concurrent duplicate app payment id, duplicate/out-of-order callbacks, callback arriving before the worker's response, expired-lease recovery, daily-summary SQL.
- Acme client: test against the stub or a fake `HttpMessageHandler` for 429/`Retry-After`, 5xx, timeout (never a new `TransactionID`), RTTQ 404.

## Logs
- Verify logs only when they carry business meaning (IDs, outcomes), not workflow chatter.

## When tests are required
- Required: logic-bearing code (handlers/services/validators/mappers with rules/repositories/clients).
- Not required: config-only changes, pure refactors with no behaviour change, data-carrier-only types.

## Always
- Run the tests you add and report the result. Prefer test-first for bug fixes (reproduce, then fix).
