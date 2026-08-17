# CockroachDB Managed MCP Server — setup

Sentinel uses the Managed MCP Server for **safe, read-only operational
introspection** of its memory layer from Claude Code / Cursor / VS Code:
inspecting the schema, querying incident history, and verifying audit rows —
with full audit logging on the CockroachDB side and zero write access.

## Steps

1. Log into the CockroachDB Cloud Console and select your Sentinel cluster.
2. Open the **MCP** tab and copy the config snippet (endpoint: `https://cockroachlabs.cloud/mcp`).
3. Paste it into your client config, e.g. for Claude Code (`.mcp.json`):

```json
{
  "mcpServers": {
    "cockroachdb": {
      "type": "http",
      "url": "https://cockroachlabs.cloud/mcp",
      "headers": { "Authorization": "Bearer <your-cloud-api-key>" }
    }
  }
}
```

4. The server is read-only by default. Keep it that way for Sentinel — the
   agent's writes go through the audited application path, never through MCP.

## Useful prompts once connected

- "Show the schema of the sentinel database."
- "How many incidents were resolved in under 5 agent steps this week?"
- "List the last 10 agent_actions for incident <id> in order."
