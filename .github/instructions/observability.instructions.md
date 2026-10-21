---
applyTo: "**/*Metric*.cs,**/*Telemetry*.cs,**/*Observability*.cs,**/*Publisher*.cs"
---

# Metrics & Observability

## Cardinality — the most important rule
**Never use unbounded identifiers (user id, device id, request id, serial number) as metric tags/labels.** Unbounded series overflow or crash metrics backends and silently corrupt aggregates.

- Series count multiplies across tags. Estimate the product before adding a tag; if it exceeds ~50,000, redesign.
  `customer (1,000) × eventType (50) × env (10) = 500,000` → borderline. `customer × deviceId` → never.
- Per-entity detail belongs in **structured logs / event datasets**, not metrics.
- Distinct counts across all entities: **pre-aggregate at ingest** (e.g. per-customer gauge, set reset on a schedule) instead of query-time distinct over high-cardinality tags. If scaled to >1 instance, move the set to a shared store (e.g. Redis `SADD`/`SCARD` with TTL) without changing metric names/tags.

## Instruments
- Use the repo's shared `Meter`; declare instruments as `static readonly` fields, not in constructors.
- Name metrics `<service>.<noun>.<verb>` consistently; document units; keep names stable (dashboards depend on them).
- If you can't use the shared meter, say so and why in the change summary.

## Logs & traces
- Structured logs with correlation ids; propagate trace context across messages/HTTP.
- Log business outcomes at Information, not every internal step.
