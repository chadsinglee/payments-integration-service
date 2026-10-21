---
applyTo: "**/Services/**,**/Consumers/**,**/Lambdas/**,**/*Consumer*.cs,**/*Handler*.cs,**/*Worker*.cs"
---

# Message-Driven Services & Handlers
<!-- Stack pack: for Kafka/SQS/queue consumers, workers and Lambdas. Language-neutral ideas; examples are .NET. -->

## Flow
```
Consumer (hosted service / Lambda)
  1 deserialize  2 validate transport shape  3 boundary filters
  4 map external shape -> application-owned shape
  5 build typed Command  6 dispatch to typed Handler
Handler: use-case workflow, decisions, external calls/persistence
```

## Invariants
1. **Map external shapes to application-owned shapes at the boundary**; external contracts never flow into handlers.
2. **Consumers contain no business workflow** — only deserialize, validate shape, filter, build command, dispatch.
3. **Handlers receive their own typed command** — never raw messages, DTOs, or shared base types; no runtime casting.
4. **The consumer builds the command**; the handler doesn't know where it came from.
5. Resolve cross-cutting identity (e.g. provider, tenant) **where first needed**, not eagerly by callers.

## Filter vs Policy
- **Filter** (`*Filter`): skips on message/infrastructure traits (wrong env, unsupported type). No business knowledge. Lives at the boundary.
- **Policy** (`*Policy`): decides on application rules/state (account not eligible, feature off). Lives in the application layer.
- "This message doesn't belong here" → Filter. "This entity doesn't meet a condition" → Policy.

## Reliability
- **Idempotent handlers** (at-least-once delivery is the norm): dedupe by key or make writes upserts.
- Commit offsets / ack **after** successful processing (or after durable hand-off).
- Poison messages: bounded retries with backoff, then dead-letter with context; never block the partition forever. Dead-letter queues must be monitored and alerted on, with a named owner to resolve them; dropping messages silently is never acceptable.
- Bound concurrency, batch sizes and in-memory buffers; flush on size **and** time.
- Graceful shutdown: honour cancellation, finish/flush in-flight work before exit.
- Scheduled/one-shot jobs that must not overlap use a distributed lock (e.g. Postgres advisory lock) held for the whole run.

## Anti-patterns
- Orchestrator classes mixing admission checks with dispatch and growing use-case logic.
- Factories that resolve concrete handler types (often a renamed orchestrator) — dispatch with a switch/routing on the consumer side.
- Passing event models straight into handlers.
- Configuration that is silently ignored — tuning values belong in the configuration source the service actually reads (document which); bootstrap-only values (config URL, secret ARN) are the only environment variables.
