---
applyTo: "**/*.cs,**/*.csproj,**/Directory.Build.props,**/.editorconfig"
---

# .NET Conventions
<!-- Stack pack: keep for .NET repos, delete otherwise. Repo-specific deviations belong in AGENTS.md. -->
> **Precedence:** spec/domain constraints (security, compliance, correctness) in `SOLUTION.md` override this file. The "House style" section at the bottom holds preferences, not industry standards — edit or delete freely.

## Style (enforce via .editorconfig / analyzers; don't hand-police in reviews)
- `_camelCase` private fields; `PascalCase` constants; `var` except primitives; blank line between properties in classes/records.
- `sealed` by default for classes not designed for inheritance (especially infrastructure implementations).
- Pattern matching over `as`/`is` + null check.
- Avoid ambiguous "unset" values: give enums an explicit named zero value (e.g. `None`/`Unknown`) and compare against it by name, not `== default`.
- Prefer modern C# (collection expressions, records for data carriers, `init` properties, file/block-scoped namespaces per the repo's `.editorconfig`).
- Nullable reference types enabled; don't suppress warnings with `!` without a comment.

## Async & cancellation
- No `async void` (except event handlers). Never block with `.Result` / `.Wait()`. Never fire-and-forget without handling failures.
- Await every Task — un-awaited tasks hide exceptions.
- New I/O-bound public methods accept a `CancellationToken` and pass it through. Don't retrofit `= default` parameters onto existing signatures unrelated to the task. Don't let cancellation interrupt an operation that must complete once started (e.g. after an irreversible external call has been sent) — complete it or record its outcome.

## Errors
- No swallowed exceptions (`catch { }`). Catch only what you can handle; log with context or rethrow (`throw;`, not `throw ex;`).
- Don't use exceptions for expected control flow; validate at the boundary and return a typed result/status.
- No error handling for impossible scenarios.

## Logging
- `ILogger<T>` with **structured named parameters** (`{OrderId}`), never string interpolation or `{0}`.
- Levels: Error = failure, Warning = recoverable, Information = business events, Debug = diagnostics.
- Include correlation context (trace id, message type) in error logs. Never log secrets, tokens, or full payloads.

## Configuration & DI (composition root)
- `Program.cs` is a thin entry point; move registrations to extension methods on `IServiceCollection` (e.g. `ConfigureApplication()` / `ConfigureInfrastructure()`).
- Settings classes live next to the concern they configure (not in a generic `Config/` folder).
- Constructor injection only; no service locator (`GetService`) in business code. Check lifetimes (no scoped into singleton).
- **New service/handler ⇒ verify it's registered.** Missing registrations are not caught by unit tests and fail at runtime — add a DI smoke test (build the host and resolve the graph) where practical.
- Secrets from a secret store / environment, never `appsettings.json`.

## Performance (hot paths only — don't micro-optimise elsewhere)
- Prefer `Span<T>`/`Memory<T>`, `AsSpan()` over `Substring`, `TryGetValue` over `ContainsKey`+indexer, capacity-initialised collections, `StringBuilder` only when needed.
- Avoid LINQ allocations in hot loops; avoid boxing; reuse `HttpClient` via `IHttpClientFactory`; pool large buffers (`ArrayPool<T>`).
- When fixing a bug in a hot path, note performance improvements alongside the fix and cover them with tests.

## Dependencies & project hygiene
- Pin/centralise package versions (`Directory.Packages.props`). Don't add a package for something the BCL does.
- With central package management, `dotnet add package` fails or misplaces versions: add the `<PackageVersion>` to `Directory.Packages.props` by hand, reference it without a version, then restore.
- Multi-target only when a package is consumed by older runtimes (e.g. `netstandard2.0` for shared contracts/NuGet clients).
- Build must be warning-clean for new code; run `dotnet format --verify-no-changes` (or the repo's CI equivalent) before finishing.

## IDs & data
- Be explicit about ID types (e.g. signed 64-bit); never assume positivity unless documented.
- Don't compare floating-point/dates with `==` without a tolerance/UTC convention. Store/transport timestamps as UTC (`DateTimeOffset` or UTC `DateTime`).

## XML docs
- Public/internal API of libraries and shared packages: XML docs (`<summary>`, `<param>`, `<returns>`, interface methods too) if the repo opts in — state the choice in `AGENTS.md`.

## House style (preferences — edit or delete per repo)
- Inject the plain settings POCO instead of `IOptions<T>`: bind once at the composition root with `services.AddSingleton(sp => sp.GetRequiredService<IOptions<T>>().Value)`. Keeps the options framework out of business code and simplifies tests. Use `IOptionsMonitor<T>` where config must reload at runtime.
