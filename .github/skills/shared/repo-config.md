# Repo Config (the ONLY file skills read for repo-specific values)
<!-- Fields with no counterpart (no git host, tracker, observability tool) are set to `n/a`, not left as {{…}}. -->
<!-- Fields unknown but expected later are `TODO(confirm)`. -->


Fill this in once per repo. Skills reference these keys and must not hard-code them.

| Key | Value |
|---|---|
| Default branch (PR target) | `main` |
| Git host | GitHub |
| Host CLI / MCP for PRs | `gh` |
| Repo / project identifiers | `chadsinglee/payments-integration-service` |
| Issue tracker | none |
| Tracker site / cloud id | n/a |
| Ticket key regex | n/a |
| Ticket URL template | n/a |
| Ticket key placement | n/a (no tickets) |
| Merge strategy | merge |
| Auto-complete PRs? | no — developer completes manually |
| Release tag pattern | n/a (no release process in the spec) |
| Release branch pattern | n/a |
| Build command | `dotnet build Payments.sln` (verified) |
| Unit test command | `dotnet test tests/Payments.UnitTests` (verified) |
| Reference implementation (peer to copy) | `submit-and-approve` slice: `PaymentSubmissionService` / `PostgresPaymentStore` / `AcmeClient` and `tests/Payments.IntegrationTests/Flow/SubmitAndApproveTests.cs` (pending user confirmation) |

## Observability (no observability tool; `regression-check` was removed)
| Key | Value |
|---|---|
| Observability tool / MCP | none (local logs only; see SOLUTION.md for the AWS signals) |
| Dataset / index / log group | n/a |
| Service identifier field | n/a |
| Error level field and value | n/a |
| Team / tenant filter (if shared dataset) | n/a |
| Time parameter format | n/a |
| Deploy reference source | n/a |
| Services (identifier → source path) | n/a |

## Branch prefixes
| Purpose | Prefix | Example |
|---|---|---|
| Feature / story / task | `feature/` | `feature/payment-submit` |
| Bug | `fix/` | `fix/duplicate-notification` |
| Bugfix without ticket | `fix/` | `fix/null-check` |
| Tooling / AI config | `chore/` | `chore/update-skills` |
