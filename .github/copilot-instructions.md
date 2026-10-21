# Copilot Instructions

Primary context is in [`AGENTS.md`](/AGENTS.md) — read it first. This file only adds Copilot-specific pointers.

## Per-area rules (auto-loaded via `applyTo`)
- Tests → `.github/instructions/testing.instructions.md` (read in full before writing any test)
- Commits → `.github/instructions/commit-guidelines.instructions.md`
- Docs upkeep → `.github/instructions/docs-maintenance.instructions.md`
- Add more as `<area>.instructions.md` with an `applyTo` glob (e.g. startup/composition root, API controllers, data access).

## Skills / agents
- Skills: `.github/skills/` (settings in `shared/repo-config.md`)
- Agents: `.github/agents/`

## Output expectations
- Code should look like it was written by a competent engineer working inside established constraints. Over-engineered or speculative solutions are considered incorrect.
- Finish with a short summary: what changed, how it was verified, any deviations or unverified items.
