# Branch Naming & PR Title Rules

Prefixes, key regex and ticket URL come from `repo-config.md`.

## Rules
- Ticket-based: `<prefix><TICKET-KEY>` only — never include the ticket summary.
- Ad hoc: `<prefix><short-kebab-description>`.
- Detect type by matching the prefix (Feature / Bugfix / Tooling); otherwise ask the user.
- Extract the ticket key by scanning the branch name with the key regex.

## PR title by branch type
| Branch | Title | Tracker lookup |
|---|---|---|
| Feature `<KEY>` | `<KEY>: {ticket summary}` | Required |
| Bugfix `<KEY>-…` | `<KEY>: {ticket summary}` | If key found |
| Bugfix (no key) | `Bugfix: {readable branch name}` | No |
| Tooling | `Tooling: {readable branch name}` | No |
| Other | Ask the user | No |

- **Readable branch name:** strip prefix, hyphens/underscores → spaces, capitalise first word.
- **Summary formatting:** replace `:` inside the ticket summary with ` -` (avoids double colons in the title).
