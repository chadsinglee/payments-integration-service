# AI working artifacts

Plans, prompts, task breakdowns, review notes and exported sessions (personal information removed) for this project, as required by `docs/spec.md`.

- `implementation-plan.md`: written by the `scaffold-from-spec` skill.
- `transcripts/`: **full session transcripts**, mechanically converted from the raw Copilot CLI event log and redacted (personal data, local paths, credentials). They cover the bootstrap and all nine slices, one file per slice (see `transcripts/README.md` for what is included, cut and redacted).
- `sessions/`: **curated summaries** written by hand for slices 6-9 (`timeout-and-reconciliation`, `throughput-protection`, `daily-summary`, `hardening-and-deliverables`). They are summaries, not transcripts. Slices 1-5 (`scaffold-and-schema`, `submit-and-approve`, `containers`, `declines-and-duplicate-submit`, `callbacks-and-lifecycle`) have no curated summary; use their transcripts, `implementation-plan.md` and `SOLUTION.md`.
- The session's context-checkpoint summaries are working notes for the AI and are neither committed nor part of the record.
- `spec-conformance.md`: final traceability table from the planner agent.
