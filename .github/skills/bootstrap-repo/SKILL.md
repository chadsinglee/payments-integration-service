---
name: bootstrap-repo
description: Tailor this AI starter template to a new project from its spec. Fills AGENTS.md, repo-config.md, testing/architecture rules, and prunes unused skills. Use for "bootstrap this repo", "set up AI config from the spec", "tailor the template".
---

# Bootstrap Repo From Spec

Run once per new project (re-run safely to refresh). Never invent facts: anything not in the spec, code, or user answers stays as a visible `TODO(confirm)` and is listed in the final report.

## Inputs
- Project spec (default search: `docs/spec*.md`, `SPEC.md`, `README.md`, or a path/URL the user gives).
- Existing code/manifests, if any (`*.sln`, `package.json`, `pyproject.toml`, `go.mod`, pipelines, Dockerfiles).

## How to present (applies to every step)
- **Announce each step** before doing it, as a heading with its number, total and name, e.g. `Step 7 of 14: Architecture decisions`, plus one sentence on what it does and what you need from the user (if anything). Say when you finish a step and what comes next.
- **Never present bare options.** No "Option A/B/C", "1/2/3" or other letter/number-only choices. Every option has a descriptive name (e.g. `SQLite file`, `Postgres`), and the form itself must show, for each option: what it is in one sentence, the main pro, the main con, and which spec requirement it affects. Mark the recommended option and give the reason. Put this text in the option titles/descriptions of the `ask_user` form, not only in prose above it, so it is visible while choosing. If the form can't hold it, print a short comparison table first, then ask.
- Use the same plain names when recording decisions, and when referring back to a step or decision (say "the Architecture decisions step", not "step 5c").

## Steps
1. **Read the spec** fully, then scan the repo for stack, structure, build/test commands, CI config and existing conventions. Summarise your understanding in ≤10 lines.
2. **Ask what is unknown** (ask_user, one batched form) only what's still unknown and matters: git host, default branch, issue tracker + key format, merge strategy, branch prefix/team name, test framework, integration-test tagging. Do **not** ask for an architecture style here; it is decided with tradeoffs in the Architecture decisions step. Offer detected values as defaults.
3. **Fill `AGENTS.md`:** purpose, commands (verify each by running build/unit-test where possible), doc links, gotchas *only if known*. Keep < ~150 lines.
4. **Fill `.github/skills/shared/repo-config.md`** from answers; set `Reference implementation` once one exists (else TODO).
5. **Draft `docs/ARCHITECTURE.md`** from the spec: layers/modules, dependency direction, boundaries, where new code goes. Mark undecided items as open questions — don't decide architecture silently; propose options.
6. **Domain constraints:** extract into a "Domain constraints" section of `ARCHITECTURE.md` (each with a short descriptive name, e.g. `no-visitor-data`, not C1/C2) (and one-line gotchas in `AGENTS.md`): anything in the spec about correctness-critical data (money, quantities, identity), security/privacy/compliance regimes, audit and retention, availability/latency targets, and failure semantics (what must never be lost, duplicated or retried). These override the generic stack packs. **Never edit the pack files to do this.** Add a "Pack overrides" table to `ARCHITECTURE.md` (pack rule → what this repo does instead → spec reference) and list each conflict for the user to confirm. Also record spec-required patterns the packs don't cover (e.g. outbox, idempotency keys, webhook verification, append-only ledgers) in `ARCHITECTURE.md`, not in the packs. If the spec states no constraints, say so explicitly rather than inventing any. Also extract any **conventions and deliverables the spec mandates** (naming, folder layout, coding or testing standards, required docs, CI, packaging, release artefacts). Where one conflicts with a template file (`AGENTS.md`, instruction packs, skills, `repo-config.md`), the spec wins: adapt the template file, list each change for the user to confirm, and record it in the "Pack overrides" table or as an addition to `AGENTS.md`. **Where things are documented is the spec's call, not the template's:** if the spec names a file or format for the explanation, design, decisions, setup or any other deliverable (e.g. a `SOLUTION.md`), use that file and that structure, and have `AGENTS.md` point to it. `docs/ARCHITECTURE.md` is only the default when the spec says nothing. Keep a "Deliverables" checklist (in whichever file holds the design) listing each required item and where it lives, so `scaffold-from-spec` builds and verifies each one.
7. **Architecture decisions (collaborative, blocking):** pick the 3–6 decisions that actually shape this project (e.g. layering/style, sync vs async, storage, tenancy, consistency and failure handling, hosting model). Skip anything the spec already fixes. For each, have the `planner` agent draft 2–3 options with pros, cons, the spec constraint it affects and a recommendation (it runs on the stronger model; give it the spec, the constraints and the decision names), then present them to the user and ask them to choose (ask_user, one decision at a time or one short batched form). Record each under a "Decisions" section of `ARCHITECTURE.md`, with a short descriptive kebab-case name instead of a number (e.g. `click-recording-async`, `storage-sqlite`): choice, reason, rejected options. Do not proceed to the diagram step with a decision open; if the user defers one, mark it `OPEN` and say it blocks `scaffold-from-spec`.
8. **Architecture diagram (before any code):** once decisions are settled, show the user a diagram of the whole system as it will be built: layers/modules with dependency direction, external systems and stores, and the 1–2 most important runtime flows (e.g. the request path and the async/background path), with the decision that shaped each part labelled by its descriptive name (e.g. `storage-sqlite`). Use plain ASCII or a Mermaid block so it renders in the terminal or on GitHub. **Print the diagram in the console response itself** (in a fenced code block), not only in the file: the user must see it in the conversation without opening anything. Save it in `ARCHITECTURE.md` under "Diagram" as well. Have the `planner` agent critique the decisions, diagram and domain constraints, and show its findings to the user (fix or ask about each one), then ask the user to confirm or request changes (ask_user). Don't continue until they confirm; update the diagram if decisions change.
9. **Tailor rules:** adapt `testing.instructions.md` to the chosen framework/assertion library/categories; replace the generic Step 3 list in `code-review/SKILL.md` with the real architecture rules (link ARCHITECTURE.md). Add `applyTo` instruction files only for areas that genuinely need rules.
10. **Trim skills (keep by default, confirm before removing):** idle skills cost little (only name + description are loaded until invoked), so remove a skill only when it clearly can't apply. The real token costs are always-loaded files and MCP tool definitions — prioritise keeping MCP servers to those actually needed and `AGENTS.md` short. For each skill, decide Keep / Remove from the evidence below and present the table via ask_user. **Delete nothing until the user confirms**; when unsure, Keep and flag it.

   | Skill | Keep if (evidence) | Remove if |
   |---|---|---|
   | start-work-item | An issue tracker is named in the spec/answers | No tracker (work is ad hoc) |
   | commit-changes | Any git repo where an agent commits | Humans always commit by hand |
   | create-pull-request | PR-based workflow (git host with PRs) | Trunk-based / direct pushes |
   | review-pull-request | PR host has a comment/thread API or MCP | No PR tooling available |
   | code-review | Always (unless solo throwaway project) | — |
   | create-release | Spec mentions versioned releases, tags, deployables, changelog | Internal app/prototype with no release process |
   | codebase-tips | Multi-module repo or multiple contributors | Tiny single-purpose repo |
   | code-reviewer agent | `code-review` is kept | `code-review` removed |
   | planner agent | `bootstrap-repo` or `scaffold-from-spec` is kept | Both removed |
   | scaffold-from-spec | New/greenfield project or large new modules | Mature repo with no scaffolding need |
   | regression-check | Services are deployed AND an observability MCP/log tool is named | No deployed services or no log/metrics tool |
   | api-explorer | Repo has HTTP client libraries for external/internal APIs with specs | No API integrations |

   If the user didn't state a tracker/host/release process and the spec is silent, ask rather than infer. After pruning, update the README skill table and `repo-config.md` rows that no longer apply. If the spec involves a database or relies on fast-moving libraries, mention the optional MCP servers in `.github/mcp-servers.md` and let the user decide; add none by default.
11. **Stack packs** (path-scoped, cost nothing unless matching files are touched): keep from the evidence, delete the rest after confirmation. If the user explicitly answers "keep everything", their answer wins over this deletion rule; list the unused packs as candidates instead.
    - `dotnet.instructions.md` — any `.cs`/`.csproj`.
    - `dotnet-api.instructions.md` — ASP.NET controllers/endpoints or HTTP client libraries.
    - `messaging-services.instructions.md` — queue/stream consumers, workers, Lambdas.
    - `observability.instructions.md` — metrics/telemetry code.
    - `database-postgres.instructions.md` — Npgsql/EF/Dapper + Postgres or `.sql` migrations. For other DBs, adapt it rather than keep it as is.
    Fill each pack's `{{…}}` placeholders (e.g. tenant/auth attribute) from the spec, and reconcile with `.editorconfig`. A pack whose technology the spec replaces (e.g. a different database) or that has no matching files should be deleted or rewritten for the real technology, not kept with an override: path-scoped packs still inject their text whenever matching files are touched.
12. **Verify:** no `{{…}}` left except intentional `TODO(confirm)` (in kept-but-unused skills' registry rows and in `repo-config.md` fields with no counterpart, write `TODO(confirm)` or `n/a`, never a raw `{{…}}`); every command in `AGENTS.md` was run or flagged unverified; every link resolves.
13. **Replace `README.md`:** the copied README documents the template, not the project. Replace it with a short project README (purpose from the spec, how to build/test from `AGENTS.md`, links to `docs/spec.md` and `docs/ARCHITECTURE.md`). Move the template README to `docs/ai-template.md` only if the user wants to keep it.
14. **Report:** what was filled, what was assumed, open `TODO(confirm)` list. Do **not** commit — show the diff summary and wait. Mention that `scaffold-from-spec` is the next step for generating code once open architecture questions are resolved.

## Rules
- Follow `AGENTS.md` working agreements while doing this (surgical, no invention).
- Prefer the spec over assumptions; quote spec sections when stating constraints.
- Keep always-loaded files small; move detail to `docs/`.
- The template repo is the source of truth. When refreshing, merge deliberately; never bulk-copy it over files already tailored to this project.
