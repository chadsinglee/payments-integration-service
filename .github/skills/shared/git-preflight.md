# Git Preflight Checks

Run before any skill that branches, commits, or opens a PR.

1. **Inside a repo?** `git rev-parse --git-dir` — if it fails, stop: "Not in a git repository."
2. **Uncommitted changes?** `git --no-pager status --porcelain`. Ignore untracked (`??`). If modified/staged files exist, ask: stash / commit first / proceed anyway.
3. **Current branch:** `git --no-pager branch --show-current`
4. **Upstream set?** `git --no-pager rev-parse --abbrev-ref --symbolic-full-name "@{u}"` — on error the branch isn't pushed.
   Tell the user: "Branch `{branch}` isn't pushed. Push with `git push -u origin {branch}` then re-run." **Never push automatically** unless the skill has an explicit, user-confirmed push step.

Always use `--no-pager` so output isn't swallowed by a pager.
