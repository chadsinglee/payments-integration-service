# payments-integration-service — Agent Context

> Canonical, tool-agnostic instructions. Read by Copilot, Cursor, Codex and others.
> Keep this file **short (< ~150 lines)**: it is loaded into every session. Put detail in linked docs.

## What this repo is
A C#/.NET 8 service that lets the Zapper app submit and track card payments through the PSP Acme Pay (stored card tokens, facilitator sub-merchants), processes Acme's Payment Advice notifications and Transaction Status webhooks (duplicate/out-of-order safe), and produces a daily per-sub-merchant settlement summary. Includes an Acme stub and a container setup. It is an assessment: the spec is `docs/spec.md`.

## Commands
All commands verified (slice 9). The SDK is pinned to 8.0.x via `global.json`.
| Task | Command |
|---|---|
| Restore / install | `dotnet restore Payments.sln` |
| Build | `dotnet build Payments.sln` |
| Unit tests (no external infra) | `dotnet test tests/Payments.UnitTests` |
| Integration tests (need Docker/DB) | `dotnet test tests/Payments.IntegrationTests` |
| Customer simulation (needs the stack running, see `docs/customer-simulation-spec.md`) | `dotnet test tests/Payments.CustomerSimulation` |
| Lint / format | `dotnet format Payments.sln --verify-no-changes` |
| Run locally | `docker compose up --build -d` (Api on :5001, stub on :5002) |

## Where things live
- Design, decisions, domain constraints, pack overrides, diagram, Deliverables checklist: `SOLUTION.md` (the spec names it as the design document; there is no `docs/ARCHITECTURE.md`)
- Spec (authoritative): `docs/spec.md`
- AI working artifacts (plans, prompts, task breakdowns, review notes, exported sessions with personal info removed): `ai/`. The implementation plan lives at `ai/implementation-plan.md`.
- Per-area rules (auto-loaded by path): `.github/instructions/*.instructions.md`
- Reusable workflows: `.github/skills/*/SKILL.md` — repo settings live in `.github/skills/shared/repo-config.md`

## Working agreements
1. **Think before coding.** State assumptions. If several interpretations exist, list them; don't pick silently. Ask when unclear.
2. **Simplicity first.** Minimum code that solves the problem. No speculative features, single-use abstractions, or unrequested configurability.
3. **Surgical changes.** Every changed line must trace to the request. Don't reformat, refactor or "clean up" adjacent code. Mention unrelated dead code; don't delete it. Remove only what *your* change made unused.
4. **Follow existing patterns.** Find a comparable implementation first; prefer consistency over novelty.
5. **Goal-driven.** Turn the task into verifiable success criteria. Bug fix → failing test first. Refactor → tests green before and after. Run the tests you add.
6. **Don't invent architecture.** Implement within the established design; propose changes instead of making them.
7. **Verify, don't assume.** Build and run the smallest relevant test set before declaring done. Report anything you couldn't verify.
8. **Report deviations.** If you couldn't follow an instruction exactly, say so and why in the summary.
9. **Use the edit tool for files.** Don't rewrite source with shell string replacement (fragile with quotes, braces and `$`); re-view the file after an edit near string literals. Some file-creation tools need the parent directory to exist and can't overwrite: create the directory first, and delete a file before recreating it.
10. **Make choices and progress legible.** When a skill has steps, announce each as "Step N of M: <name>". When asking the user to choose, never offer bare "A/B/C" or "1/2/3": give each option a descriptive name plus a one-line description, its main pro and con, and a recommendation, inside the question form itself.
11. **Model roles.** Everything runs on the model selected for the session. The `planner` and `code-reviewer` agents pin `model:` in their frontmatter (currently `claude-opus-5.5`) because planning and review benefit most from the strongest reasoning. If that model isn't available on your account, change or remove the `model:` line in `.github/agents/*.agent.md`; the agent then inherits the session model.

## Precedence
1. The spec (including its mandated deliverables: `README.md`, `SOLUTION.md`, `ai/`) and the "Domain constraints" in `SOLUTION.md` win over everything below.
2. `.github/instructions/*.instructions.md` stack packs are general defaults. If a spec constraint conflicts with one, follow the spec and **say so in your summary**; propose updating the pack for this repo.

## Safety rails
- Never commit secrets, tokens, or real customer data. Use env vars / secret stores.
- Never push, force-push, merge, create PRs/tickets, or delete branches without explicit user confirmation (see consent gates in skills).
- Prefer read-only tools/MCP servers for investigation; don't call raw production APIs when an MCP/tool exists.
- Only run tests that don't need external infrastructure unless told otherwise.

## Gotchas (keep this list real — add only things that have bitten someone)
- Don't batch dependent file operations (create a folder then a file in it, or delete then create the same path) in one parallel tool call; they race.
- Money is `long` minor units (`1999` = R19.99), never float/decimal-from-double. `CurrencyCode` defaults to ZAR when absent.
- Acme `TransactionStatus=Success` with `ResponseDetail.Code != 00` is a **decline**. Outcome is in the code, not the status.
- Acme timestamps are SAST (UTC+2) with no offset; convert explicitly, store UTC.
- Never resubmit a timed-out payment under a new `TransactionID`; query RTTQ instead.
- Callbacks are public: verify Basic auth (advice) / HMAC-SHA256 of the raw body bytes (webhook, constant-time compare) before parsing; ignore unknown JSON fields.
- Store card tokens only; never log tokens, API keys, secrets or full card data.
- Exported AI sessions committed to `ai/` must have personal information removed.

## Keeping docs in sync
See `.github/instructions/docs-maintenance.instructions.md`. Code changes that alter documented knowledge update the docs **in the same change**.
