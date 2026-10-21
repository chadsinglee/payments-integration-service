---
applyTo: "**"
---

# Keeping Documentation in Sync

Docs that agents rely on (`AGENTS.md`, architecture/index docs, optional knowledge bundle) are **living documents**. After any code change, check whether it affects documented knowledge and update docs **in the same change** — not as a follow-up.

## Triggers an update
- New / removed / renamed projects or modules
- Public API changes (endpoints, signatures, status codes, error contracts)
- New or retired architectural patterns
- Domain entity / DTO changes
- New infrastructure dependencies or configuration keys

## Does NOT trigger an update
- Private refactors, bug fixes with no observable change, test-only changes, comment/formatting edits

## How
- Edit only the affected docs; keep any `index.md` lists in sync.
- Append a dated entry (`YYYY-MM-DD`) to the changelog/log file if the repo keeps one.
- Never delete a knowledge file — mark it `status: deprecated` with a reason.
- Record provenance in frontmatter when materially updating: `generated: { by: <tool/model or human:id>, at: <ISO-8601> }`.

## Size discipline
- `AGENTS.md` stays short (map + rules + commands + gotchas). Push detail into linked docs; large always-loaded files waste context in every session.
