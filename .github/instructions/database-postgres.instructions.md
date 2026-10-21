---
applyTo: "**/*.sql,**/Schema/**,**/Migrations/**,**/*Repository*.cs,**/DataAccess/**,**/*Store*.cs"
---

# PostgreSQL & Data Access Conventions
<!-- Stack pack: keep for repos using PostgreSQL. Derived from Flyway-managed Postgres schemas, Npgsql/Dapper-style access and hot/cold data tiering. -->

## Schema changes = versioned migrations only
- Every schema change is a new, immutable migration (Flyway naming `V<major>_<minor>__Description.sql`, in a `Schema/v<major>/` folder next to the data-access project). **Never edit a migration that has been applied anywhere.**
- Migrations are forward-only and safe to run while the old version of the app is still running (expand → migrate → contract; additive first, destructive later).
- Each migration does one thing. Include indexes with the table that needs them.
- Include ownership/grants explicitly for the app role(s) — least privilege (`SELECT/INSERT/UPDATE/DELETE` only; no DDL for runtime roles).
- Large/hot tables: add indexes `CONCURRENTLY` (outside a transaction block), batch backfills, set `lock_timeout` so DDL fails fast instead of blocking production.
- Test migrations from empty and from the previous version.

## Naming & types
- Lower-case, unquoted identifiers (`snake_case` or consistently lower-cased); name indexes `ix_<table>_<columns>`, PKs `<table>_pkey`.
- 64-bit ids (`BIGINT`); app-generated ids via the project's ID generator (not `serial`) when ids are shared across systems.
- **Timestamps: `TIMESTAMPTZ`, stored UTC.** Avoid `timestamp without time zone` for new tables (mixed use causes subtle bugs).
- `TEXT` over `VARCHAR(n)` unless a real limit exists. Flexible/extensible attributes → `JSONB` (not `JSON`/text), with a documented reserved-key convention; promote hot keys to columns.
- Status/enum values stored as small ints with a documented mapping in code.
- Be deliberate about NULL vs. unknown vs. false; don't coerce missing to a value.

## Queries & indexes
- Parameterised queries only — **never concatenate user input** into SQL.
- Index for the actual query predicates (selection columns first, then sort: e.g. `(tenant_id, region, status, created DESC)`); verify with `EXPLAIN (ANALYZE, BUFFERS)` on realistic data.
- No `SELECT *` in application queries; select named columns.
- Time-series/high-volume tables: **partition by time** (e.g. monthly range) and always filter on the partition key; plan retention by dropping partitions, not mass `DELETE`.
- Bulk writes: batch / `COPY` / multi-row insert. For idempotent ingest use `INSERT … ON CONFLICT`, choosing **per table, explicitly**: `DO NOTHING` only where a duplicate is truly identical and safe to drop; otherwise `DO UPDATE` with a documented rule, or fail and alert. Never silently discard a conflicting row whose contents differ.
- Counting: state whether duplicates/quarantined rows are excluded (`COUNT(DISTINCT id)` vs `COUNT(*)`).

## Connections & transactions
- Use the repo's connection manager; **separate reader and writer connections** where replicas exist (reads that must see a just-written row go to the writer).
- Set an `ApplicationName` on connections so `pg_stat_activity` identifies the service.
- Keep transactions short; no network calls inside a transaction; commit related changes atomically (e.g. register output + mark sources in one transaction).
- Set command timeouts; never rely on defaults for long-running work.
- Prevent overlapping scheduled jobs with **session-scoped advisory locks** (`pg_try_advisory_lock`) held across the whole run.
- Retries only for transient errors and idempotent operations.

## Repository layer rules
- Repositories abstract persistence the application owns conceptually; interface lives in the application layer, implementation in infrastructure.
- Accept/return **application models**, mapping to storage records internally. **No business rules in repositories or mappers.**
- Don't call an external team's API wrapper a "repository".
- One implementation project per store (Postgres, Redis, S3…) behind shared interfaces so tests can swap them.

## Hot / warm / cold data (if applicable)
- Document the tiers (Postgres hot → cache → object storage archive) and retention windows in `SOLUTION.md`.
- Archive/compaction jobs must be re-runnable: delete sources only **after** the output is durably written and registered; fail closed if results would shrink unexpectedly. Retention and deletion periods come from the spec/regulation, not from this file; where records must be kept (financial, audit), prefer append-only tables and never hard-delete.
- Object-store writes that must not race use conditional writes (ETag / `If-Match`, `If-None-Match`).

## Testing
- Repository/migration tests run against a real Postgres (Testcontainers) and are tagged as integration; unit tests must not need a database.
- Don't mock the database in tests meant to verify SQL.
