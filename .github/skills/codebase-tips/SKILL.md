---
name: codebase-tips
description: Save and retrieve navigational codebase knowledge — which client/wrapper to use, cross-team dependencies, non-obvious gotchas. Query ("how do I get X?") or save ("X → use Y").
---

# Codebase Tips

A growing, human-reviewed cheat-sheet of *where things are and which thing to use*. Stored in `hints/<domain>.md` next to this file.

## Intent detection
- **Query** — phrased as a question → read hints, answer from them. If nothing matches, say so; never invent.
- **Save** — factual navigational statement → draft a tip.
- **Ambiguous** — ask: "Save this as a tip, or are you looking something up?"

## Query
1. Pick the domain from keywords (if unclear, read all `hints/*.md`).
2. Return matching entries.

## Save
1. Pick domain; draft the tip against the quality bar below.
2. **Show the draft (file, heading, body) and wait for explicit confirmation before writing.**
3. Append to `hints/<domain>.md` (create with `# <Domain> Hints` if new). Confirm what was saved.

## Format
```markdown
## <Short noun-phrase heading>

<What it is, canonical source first, project-specific variants, known gotchas.>
```

## Quality bar
- Heading = short noun phrase (not a question).
- Canonical source named first; variants explicit ("In X: … Elsewhere: …").
- Gotchas included. 3–5 lines max — split if bigger.
- Factual and navigational only; no opinions or "you should".
- Promote a tip to `AGENTS.md`/docs only if it's needed in nearly every session.
