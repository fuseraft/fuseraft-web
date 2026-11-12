---
title: MCP servers
description: Add MCP tool servers once, for every account's chats and orchestrations, over Streamable HTTP.
---

An MCP server gives agents tools of its own: a database, a ticket tracker, a search index. Admins
add them on the **MCP** page, and every account can use them. You don't set them up per account,
and nobody needs to run `/mcp add`. fuseraft-web reaches them over Streamable HTTP, so each one runs
once as a shared service.

They need fuseraft 1.24.0 or later. Older versions ignore them, and the page says so.

## Add one

Admins give each server:

- **Name**: what agents list under `Plugins`, and how chats label its tools. It can't be the name of
  one of fuseraft's own plugins, such as `FileSystem`, and it can't be changed later.
- **URL**: the server's Streamable HTTP endpoint, as this server reaches it, such as
  `http://localhost:8080/mcp`.
- **Token**: optional; sent as `Authorization: Bearer`. It's saved in the data directory, encrypted,
  and never shown again.

**Test and add** connects the way fuseraft will. It runs the MCP handshake, lists the server's
tools, and says what's wrong if it can't. That might be a refused token, a wrong path, or a server
that only speaks the older SSE transport. **Add without testing** saves a server that isn't up yet.
**Test** on a saved server checks it again. **Edit** changes its URL or token, and **On/Off** takes it
out of use without forgetting it.

Everyone sees the list and each server's name and URL, so they know what to put in `Plugins`. Only
admins see the controls.

## Where they're used

- **Chats** connect every server that's on when they start. A server that's down makes the chat
  print a warning, and it carries on without that server's tools.
- **Orchestrations**, run from Sessions or the Schedule, connect a server only when one of their
  agents lists its name under `Plugins`:

  ```yaml
  Agents:
    - Name: Analyst
      Plugins:
        - Sql
  ```

  If the server can't be reached, the run logs a warning and the agent works without its tools. A
  config that declares its own `McpServers` entry with the same name uses that entry instead.

Chats and runs already going keep what they started with. A change applies to the next one.

Tokens reach fuseraft through its environment, never through a file, and fuseraft hides their
values from agents' tool output. Agents run as this server's OS user, though, so treat a token like
any other secret the server holds: give each MCP server a token for fuseraft-web alone, with no more
access than agents should have.

## Example: sql-mcp

[sql-mcp](https://fuseraft.ai/sql-mcp/) lets agents read SQL Server, PostgreSQL, MySQL and Oracle
databases, and nothing more. Run it as a service with a token for fuseraft-web:

```bash
export App__AgentTokens__fuseraftweb="$(openssl rand -hex 32)"
sql-mcp   # listens on http://localhost:8080 by default
```

Then add it on the MCP page with the name `Sql`, the URL `http://localhost:8080/mcp`, and that
token. **Test and add** should list its five tools. sql-mcp logs every query under `fuseraftweb`,
whichever account's agent ran it.
