---
name: commit-changes
description: Commit the current working-tree changes with a convention-following message, after showing what will be committed and getting confirmation. Never pushes. Use for "commit this", "commit my changes".
---

# Commit Changes

> **🚫 MANDATORY CONSENT GATE — NOT OVERRIDABLE**
> Show the file list and message and wait for confirmation before committing, even in autopilot modes. Never push (see `create-pull-request`).

Format and trailer rules: `.github/instructions/commit-guidelines.instructions.md`. Branch and ticket rules: `.github/skills/shared/branch-conventions.md` and `repo-config.md`.

## Steps
1. **Preflight** items 1 and 3 of `.github/skills/shared/git-preflight.md` (inside a repo, current branch). Stop if on the default branch: ask the user to create a branch (`feature/<name>` or `fix/<name>`) first.
2. **Inspect:** `git --no-pager status --porcelain` and `git --no-pager diff` (plus `--cached`). Flag anything that looks like a secret, a build output or an unrelated change and exclude it unless the user insists.
3. **Run the gate** from `AGENTS.md` (build, unit tests, format). Don't commit on red; report the failure instead.
4. **Group** into logical commits if the diff mixes concerns (feature vs. refactor vs. docs); otherwise one commit.
5. **Draft** each message (`type(scope): Description`, ticket key per `repo-config.md`, co-author trailer). Show the files and message per commit → **STOP for confirmation.**
6. **Commit** by staging only the confirmed paths (never `git add -A` blindly), then `git --no-pager log -1 --stat` to show the result.
7. Suggest the next step: "Push and open a PR with `create-pull-request`." Do not push.
