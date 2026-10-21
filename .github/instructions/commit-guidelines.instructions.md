---
applyTo: "**"
---

# Commit Message Guidelines

Format: `type(scope): Description`

| Type | Use for |
|---|---|
| `feat` | New capability |
| `fix` | Bug fix |
| `refactor` | No behaviour change |
| `test` | Tests only |
| `docs` | Documentation only |
| `chore` | Deps, tooling, maintenance |
| `perf` | Performance |

- Scope: kebab-case, free-form (`video-trigger`, `deps`).
- Description: imperative, ~60 chars, no trailing period.
- Body (optional): explain *why*, not *what*.
- Breaking change: `!` after scope — `feat(api)!: Remove legacy field`.
- Ticket keys: see `Ticket key placement` in `.github/skills/shared/repo-config.md`.
- Add the AI co-author trailer when an agent authored the commit (format per tool, e.g.
  `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`).
