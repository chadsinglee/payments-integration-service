---
name: create-pull-request
description: Create a pull request for the current branch into the default branch. Derives title/description from branch type and ticket, shows a draft, and creates only after explicit confirmation. Triggered by "create PR", "raise PR", "open PR", "submit PR".
---

# Create Pull Request

> **🚫 MANDATORY CONSENT GATE — NOT OVERRIDABLE**
> Always show the draft and wait for explicit confirmation before creating anything — even in autopilot / auto-commit modes.

Values (default branch, host, tracker, merge strategy) come from `.github/skills/shared/repo-config.md`.

## Steps
1. **Preflight** — `.github/skills/shared/git-preflight.md`. Never push silently. If the branch has no upstream or is ahead of it, the push is part of step 6's confirmation: show `git push -u origin <branch>` and run it only after the user confirms.
2. **Branch type + ticket** — `.github/skills/shared/branch-conventions.md`.
3. **Ticket lookup** (Feature, or Bugfix with a key): fetch the summary via the tracker tool. On failure, fall back to a title from the branch name and say so.
4. **Changed files** — `git fetch origin <default>` then `git --no-pager diff origin/<default>..HEAD --name-only`. Group by layer/component.
5. **Draft**
   ```
   <One sentence: what this PR adds/changes.>

   **<Component/Layer>**
   - <specific change naming real types/tables/endpoints/files>

   **Tests**
   - <what they cover>   (omit if no test files changed)

   ---
   [<KEY>](<ticket URL>)   (omit for tooling / key-less bugfix)
   ```
   Developer-focused, scannable, no preamble or filler.
6. **Show draft + settings** (merge strategy, delete source branch, and the push if needed) → **STOP for confirmation.**
7. **Push** if confirmed and needed, then **create** via the host tool: source = current branch, target = default branch, not draft.
   GitHub: the built-in GitHub MCP server's create-pull-request tool, or `gh pr create --base <default> --head <branch> --title "<title>" --body-file <file>`.
   Don't set auto-merge unless `repo-config.md` says so.
8. **Output:** `✅ PR #<id> created: <title>` + URL + reminder to review/complete.

## Errors
- Uncommitted changes / unpushed branch → preflight guidance.
- Unknown branch type → ask Feature / Bugfix / Tooling, or accept a title.
- Not targeting default branch → confirm the intended base before proceeding.
