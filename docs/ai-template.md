# AI Repo Starter

Stack-agnostic baseline for AI-assisted coding. Targets **GitHub Copilot** (CLI, VS Code, cloud agent) out of the box; `AGENTS.md` is also read by Cursor and Codex.

## Fastest path: let the agent tailor it
Copy the template into the new repo, put the spec at `docs/spec.md`, open your agent in the repo and use:

> Use the `bootstrap-repo` skill. The project spec is `docs/spec.md`. Tailor AGENTS.md, repo-config.md, ARCHITECTURE.md, the testing rules and the code-review architecture rules to this project, prune skills we don't need, and verify the commands you write. Ask me about anything the spec doesn't answer, and don't commit.

(Tools that don't auto-load skills: add "read `.github/skills/bootstrap-repo/SKILL.md` first and follow it".)

Then, once `docs/ARCHITECTURE.md` has no open questions, generate the code. The skill shows the plan for approval, builds the foundation slice and pauses once for you to confirm it, then builds the rest, reviewing each slice and checking the final result against the spec:

> Use the `scaffold-from-spec` skill to build this project from `docs/spec.md`. Show me the plan first, follow the instruction packs, run the build and tests after each slice, and don't commit.

## Adopt manually (≈15 min)
1. Copy everything into the repo root (keep dot-folders).
2. `AGENTS.md` — fill the `{{…}}` placeholders (purpose, commands, doc links, real gotchas). Keep it < ~150 lines.
3. `.github/skills/shared/repo-config.md` — fill once: default branch, host, tracker, key regex, branch prefixes, merge strategy.
4. Keep the stack packs (`dotnet`, `dotnet-api`, `messaging-services`, `observability`, `database-postgres`) that match your stack and delete the rest; edit `testing.instructions.md` examples for your stack. Put spec-derived domain rules (security, compliance, correctness) in `docs/ARCHITECTURE.md` — they override the packs.
5. `code-review/SKILL.md` Step 3 — replace the generic architecture list with the repo's real rules (link `docs/ARCHITECTURE.md`).
6. Remove skills that clearly can't apply (e.g. `regression-check` with no deployed services or observability tool). Idle skills are cheap, so when unsure, keep them.

## Layout
```
AGENTS.md                       canonical context (all tools)
.github/copilot-instructions.md Copilot pointers
.github/instructions/*.md       path-scoped rules (applyTo)
  testing / commit-guidelines / docs-maintenance      generic
  dotnet / dotnet-api / messaging-services / observability / database-postgres
                                  optional stack packs (bootstrap-repo keeps/removes)
.github/agents/                 personas (planner, code-reviewer; both pinned to the strongest model)
.github/skills/<name>/SKILL.md  workflows
.github/skills/shared/          repo-config, git-preflight, branch-conventions (shared by skills)
.github/mcp-servers.md (GitHub MCP + personal-account setup)
```

## Skills included
| Skill | Purpose |
|---|---|
| start-work-item | ticket → branch → plan, consent-gated |
| code-review | scoped, architecture-aware review with severity-ranked output |
| review-pull-request | post review as inline PR threads, resolve on re-run |
| commit-changes | gate, group, draft message → confirm → commit (never pushes) |
| create-pull-request | derive title/description, draft → confirm → push → create |
| create-release | branch/tag + changelog |
| codebase-tips | save/query navigational knowledge |
| scaffold-from-spec | after bootstrap: plan an ordered slice list, build the foundation slice and pause once, build the rest with an independent review per slice, then a final spec-conformance check (requirement → code → test table) |
| regression-check | post-deploy: PR → deploy time → affected services → presence + new-exception checks (needs observability MCP) |
| api-explorer | read an OpenAPI spec, generate client DTOs/endpoints matching the repo's existing client conventions |

## Design principles
- **Surgical-change / simplicity / goal-driven** working agreements and a "competent engineer within constraints" quality bar.
- **Consent gates that autopilot cannot override** for push, PR creation and implementation.
- **A single `shared/` folder** for git preflight, branch conventions and tracker access, so skills reference it instead of re-describing it.
- **Path-scoped instructions** (`applyTo`) rather than one giant file; "read in full before writing tests".
- **Code-review output contract**: summary first, severity calibration, "existing patterns beat theory", no formatting nits.
- **Docs-in-same-change rule** with explicit triggers and non-triggers.
- **codebase-tips** with confirm-before-write and a quality bar.
- **MCP discipline**: no config committed, secrets never in files, servers added only when needed (see `.github/mcp-servers.md`).
- **Use MCP, never raw REST; report errors and continue**, kept in the AGENTS safety rails.
- **Stale-example check** (`examples-last-refreshed`) for style-reference files.
- **Org and repo specifics stay out of skills.** Tracker ids, key prefixes, app names, runbooks and pipeline maps live behind `repo-config.md` or skill registries. `regression-check` and `api-explorer` are generic versions.
- **Keep `AGENTS.md` small.** It is always loaded, so it stays under ~150 lines and links out to `docs/`.

## Pitfalls to avoid
1. **Copy-paste drift:** the same skill copied into many repos diverges over time. Keep this repo as the source of truth, and consider a central repo plus sync (or a plugin) once it stabilises.
2. **Bloat:** large spec files, tickets and runbooks under `.github/` cost context. Keep bulky reference data in `docs/` or fetch it on demand.
3. **Misplaced or placeholder files:** a root `SKILL.md` with template text, or skills split across `.cursor/skills` and `.github/skills`. Standardise on `.github/skills` and sync.
4. **Hard-coded identifiers** inside skills (org URLs, group ids, cloud ids). Put them in config or prompt for them.
5. **Personal preferences in repo files** ("User prefers a more modern coding style"). Keep those in user-level settings.
6. **Overlapping agents** (two reviewer personas). Prefer one persona plus a delivery skill.
7. **Tool-specific assumptions:** `.mcp.json` allowlists are ignored by VS Code (see `mcp-servers.md`).
8. **Burying traps in a huge file:** a rule like "use the config API, not raw templates" is a *gotcha*. Put such traps in `AGENTS.md`, not in a 38 KB document.

## Maintenance
- Treat this repo as the source of truth; when a repo improves a skill, upstream it here.
- Review skills quarterly; delete unused ones.
