---
name: review-pull-request
description: Review an existing pull request and post findings on it as inline comment threads plus one concise summary comment; on re-runs, close threads whose issues are resolved. Use for "review PR X", "re-review PR X".
---

# Review Pull Request

Analysis rules come from the `code-review` skill (Steps 2–9). Only **delivery** differs: findings go onto the PR instead of chat. Use plain `code-review` for chat-only reviews.

> **Consent:** posting comments is visible to others — confirm the PR and that the user wants comments posted before the first write.

## Independence
Run this in a **fresh session or the `Code Reviewer` agent**, not the session that wrote the code, so the reviewer reads the PR cold. Pass only the PR, `SOLUTION.md` and the spec. Hosts won't let an author approve their own PR; this skill comments only (see Rules).

## GitHub commands (host = GitHub)
Prefer the built-in GitHub MCP tools (pull request read, review comments); `gh` equivalents:
- Diff and files: `gh pr diff <n>`, `gh pr view <n> --json headRefOid,files,baseRefName`
- Existing threads: `gh api graphql` with `pullRequest(number:){ reviewThreads(first:100){ nodes{ id isResolved comments(first:1){ nodes{ body path line } } } } }`
- Inline comment: `gh api repos/{owner}/{repo}/pulls/<n>/comments -f body=... -f commit_id=<headRefOid> -f path=<file> -F line=<n> -f side=RIGHT`
- Summary: `gh pr comment <n> --body-file <file>`
- Resolve a thread: GraphQL mutation `resolveReviewThread(input:{threadId:"<id>"})`

## Steps
1. **Resolve the PR** (number/URL; ask if missing). Note the latest iteration/commit so comments anchor to current lines.
2. **Get the diff** with line content. For very large PRs read in chunks. Findings must cite real changed lines.
3. **Load existing threads.** Identify this skill's threads by a stable marker in the footer, e.g. `` `review-finding:{path}:{line}:{topic}` `` (use a visible-safe marker — some hosts HTML-escape `<!-- -->` comments). Also find the prior summary thread (`` `review-summary` ``).
4. **Analyse** per `code-review`. Findings only — no "done well" section.
5. **Post**: one inline thread per finding (severity icon, what, why it matters, local example of the right pattern, marker footer) + one top-level summary thread listing 🔴/🟡 items.
6. **Reconcile on re-run:** reply to and close threads whose issue no longer reproduces in the latest diff; don't duplicate still-open findings; update the summary thread.

## Rules
- No formatting nitpicks. Don't block on 🟢 suggestions.
- Never approve/reject or change vote unless asked.
- Never post secrets or large code dumps in comments.
