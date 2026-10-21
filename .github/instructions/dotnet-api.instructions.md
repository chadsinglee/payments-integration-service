---
applyTo: "**/Controllers/**,**/*Controller.cs,**/Endpoints/**,**/*.Client/**,**/*Client.cs"
---

# ASP.NET Core API & Client Conventions
<!-- Stack pack: keep for .NET HTTP APIs/clients. Add your auth/tenancy rules in the marked section. -->

## Controllers / endpoints = thin inbound adapters
- Deserialize → validate transport shape → build a typed command/query → dispatch to a handler → map result to HTTP. **No business logic.**
- Handlers receive typed commands/queries, never raw request DTOs or `HttpContext`.
- Validate at the boundary (model binding / validator); business validation lives in the application layer.

## Contracts
- Request/response DTOs are data carriers; keep them separate from application models and persistence records.
- Return consistent status codes and a single error contract (RFC 7807 `ProblemDetails`). Don't leak exception messages or stack traces.
- Version breaking changes (route or header); never change a published contract silently.
- Collections are paged/bounded; no unbounded list endpoints.

## Cross-cutting rules (fill in per repo)
- Authentication/authorisation attribute required on **every** action (default-deny; opt out explicitly).
- Tenant/account context: n/a (no tenancy in the spec).
- Health endpoint (`/health`) separate from business endpoints; no auth required for liveness only.

## Client libraries
- One method per endpoint; required context (tenant/account id) is a **required parameter**, not optional.
- Use `IHttpClientFactory`; timeouts, retries (idempotent calls only) and cancellation configured centrally.
- Clients return DTOs, not application models; they are not "repositories" even if they fetch data.
- Keep client + contract packages `netstandard2.0` (or lowest needed) if published to NuGet; bump version on contract change.

## Tests
- Controller tests cover mapping/status codes/auth attributes; handler tests cover logic (see `testing.instructions.md`).
- Reflection test: every controller action carries the required auth/context attribute.
