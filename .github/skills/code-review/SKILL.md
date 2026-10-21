---
name: code-review
description: Structured, architecture-aware code review of a branch diff, staged/unstaged changes, or specific files. Produces a summary block followed by severity-ranked findings. Use for "review my code", "code review", "check this".
---

# Code Review

Reviews changes against this repo's architecture, standards and test conventions. The repo's own docs are authoritative — read them first rather than relying on generic best practice.

## Step 1 — Scope
Get the branch: `git branch --show-current`. **Ask the user** (ask_user) which scope unless they already stated it:
- Feature branch: *All changes vs default branch (Recommended)* · Staged · Unstaged · Specific files
- Default branch: Staged · Unstaged · Specific files

```bash
git --no-pager diff origin/<default>...HEAD --name-only   # + full diff
git --no-pager diff --cached                              # staged
git --no-pager diff                                       # unstaged
```
Classify each file by layer (see `SOLUTION.md`): application / infrastructure / inbound adapter / test / config.

## Step 2 — Context
Understand intent (what feature/fix?). Find a **comparable implementation in a peer project** (`Reference implementation` in repo-config.md) before judging patterns.

## Step 3 — Architecture (highest priority)
Rules from `SOLUTION.md` (sections 3, 3.1, 5):
- Dependency direction: Domain <- Application <- Infrastructure <- Api/Worker. Domain has no dependencies; Application sees only ports; Api/Worker are composition roots with no business logic.
- Status rules, money and `CardAcceptorName` formatting live in Domain; Acme/Postgres/Redis types never leak above Infrastructure.
- Every status change goes through the `shared-outcome-path` use case (row lock, event, recompute). No direct `UPDATE payments SET status` elsewhere.
- No HTTP call to Acme inside a DB transaction, and no row lock held during one (`worker-lease-claims`).
- Resends keep the same `TransactionID`; a new `MessageID` per message. Never generate a new `TransactionID` after a timeout.
- Callbacks: verify auth on raw bytes before parsing; 200 only after commit; dedup via `payment_events` unique key.
- Money is `long` minor units; timestamps converted from SAST explicitly and stored UTC.
- No speculative abstraction or patterns not in `SOLUTION.md`; changes to decisions are proposed, not made.

## Step 4 — Pattern consistency
Handlers/services/mappers/repositories/DI registration follow the peer implementation. Flag naming drift.

## Step 5 — Standards
Check against `references/*.md` if present (coding, logging, testing standards).

## Step 6 — Tests
- Flag missing tests on changed **logic-bearing** code.
- Don't flag config-only, pure-refactor, or data-carrier-only changes.
- Flag async-assertion misuse (un-awaited / `void` tests).

## Step 7 — Change discipline
Scope creep, unrelated edits, unjustified public API changes, over-engineering, `TODO` without ticket reference.

## Step 8 — Security & resilience spot-check
Check changed code only; flag what you see, don't audit the whole repo.
- **Access control:** endpoints missing authentication/authorisation; no ownership/tenant check on resources fetched by id.
- **Injection:** string-built SQL/commands/paths/queries; unvalidated input reaching them.
- **Secrets & data:** hard-coded secrets; sensitive data in logs, errors or responses.
- **Input & deserialisation:** missing validation, unbounded collections or payload sizes, unsafe deserialisation.
- **Dependencies:** new or upgraded packages that are unmaintained or have known vulnerabilities (if tooling is available).
- **Resilience:** swallowed errors, un-awaited tasks.
- **Repo constraints:** anything in `SOLUTION.md` "Domain constraints" (security/compliance rules) that the change violates.

This is a spot-check, not a security audit; say so if the change touches auth, payments or personal data and suggest human security review.

## Step 9 — Static analysis (optional)
If SonarQube/analyzer MCP tools exist, run them on changed files; otherwise note it was skipped.

## Step 10 — Output (summary FIRST, then detail)

```
## Review · <brief description>
<N files · projects>
<one-sentence verdict>

- 🔴 `File:Line` — one line
- 🟡 `File:Line` — one line
- 🟢 `File:Line` — one line
- ✅ one phrase of something done well
```
Then detail sections: 🔴 Critical (must fix) · 🟡 Warning (should fix) · 🟢 Suggestion · ✅ Done well.

### Severity calibration
- 🔴 architecture violations, behaviour bugs, security issues, boundary leaks that will spread
- 🟡 missing tests for meaningful behaviour, pattern inconsistency, unclear ownership
- 🟢 reasonable improvements where the current approach already works and is consistent

## Rules
1. Review the full branch diff by default; **only changed code**.
2. No formatting/whitespace comments — tooling owns that.
3. Don't demand docs or rewrite the author's code; show the pattern instead.
4. No new frameworks/libraries/patterns unless asked.
5. Be specific (file:line, why it matters, the correct local example). Assume competence; keep it proportional.
6. Prefer existing codebase patterns over theoretical ideals.
