# MCP Servers

No MCP config is committed. Copilot CLI has a built-in GitHub MCP server, which covers issues, PRs and code search, so most repos need nothing here.

## GitHub
- Authenticate with `/login` in the CLI. Set `repo-config.md` to Git host `GitHub`, Host CLI `gh` (`gh auth login`).
- **Different account from the built-in server (e.g. enterprise login vs a personal repo):** add a user-level server. Copilot CLI does not expand environment variables in `.mcp.json` headers, so a repo file can't hold the token safely. With the token in a session variable, run: `copilot mcp add --transport http --header "Authorization: Bearer $env:GITHUB_PERSONAL_ACCESS_TOKEN" github-personal https://api.githubcopilot.com/mcp/`. The token is stored in plain text in `~/.copilot/mcp-config.json`, outside the repo. Remove it when finished (`copilot mcp remove github-personal`). Use a fine-grained token scoped to the one repo, with Contents, Pull requests and Issues read/write and a short expiry. Check tool names with `/mcp`.
- The `gh` CLI has its own login (`gh auth login`), separate from the Copilot login.
- Write actions (push, create PR, post review comments) stay behind the consent gates in the skills.

## Adding other servers (only when a skill needs one)
- Add a server only for a tool the project really uses (tracker, docs, database). Each server's tool definitions are always loaded and cost context.
- Never put secrets in a committed file. Keep `.mcp.json` gitignored.
- Locations: repo root `.mcp.json` or `.github/mcp.json` (Copilot CLI), `.vscode/mcp.json` (VS Code, which ignores `tools` allowlists). A wrong location fails at call time ("tool does not exist"), not at startup.
- Prefer read-only tools for investigation, and require consent before any write tool.

## Optional servers (suggest only when the spec calls for them)
| Server | Add it when | Caveats |
|---|---|---|
| Context7 (library docs) | The project depends on fast-moving libraries and the agent keeps getting APIs wrong. For .NET, Microsoft Learn is often enough. | No credentials needed. Its tool definitions load every session. |
| Postgres | The project has a real database and you want the agent to inspect schemas or check migrations against live data. | Needs a connection string, so add it at user level, never in a committed file. Use a read-only database user on a dev or test database, never production. The `database-postgres` instruction pack already covers most needs without a live connection. |