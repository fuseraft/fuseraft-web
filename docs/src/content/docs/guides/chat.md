---
title: Chat
description: Chat with fuseraft in the browser, confined to a workspace, with approvals and saved chats.
---

The **Chat** page is the same engine as `fuseraft repl` and the VS Code extension, in a browser. Each
chat is a `fuseraft repl --vscode` process speaking the JSONL bridge.

## Start a chat

Pick a **Model Profile** and a **Workspace**, and optionally a **System Prompt**, then start the
chat. Type a message and press **Ctrl+Enter** to send it. Replies stream in and render as Markdown.

With fuseraft v1.20.0 or later, you can keep typing while a reply streams. A message reaches the
model at its next call, or starts the next turn if the reply finishes first; **Stop** drops it.
`/title` and `/hitl on|off` take effect at once; other slash commands wait for the reply to end.
With an older fuseraft, the box stays locked until the reply is done.

## The workspace and approvals

The chat's tools are confined to its workspace. When the agent wants to write a file or run a
command, the chat asks first, with the command or a diff of the file, and the request also appears
on the [HITL](/fuseraft-web/guides/approvals/) page. **Stop** ends the current turn.

## Questions

When the agent is blocked on a decision that's yours to make, it asks a multiple-choice question
in the chat. Pick an option, type your own answer if the question allows it, or **Skip** to let the
agent use its best judgment. The reply waits until you do; **Stop** skips the question.

## Slash commands

Slash commands such as `/model` or `/compact` go to the CLI, which handles them as it does in the
terminal. See the fuseraft [REPL docs](/fuseraft-cli/repl/) for the list.

## Saved chats

A chat is saved to your fuseraft home after each turn. **Recent Chats** on the Chat page lists
them, and opening one resumes it. Your chats are your own: admins can't see them.

Open chats run in [relay processes](/fuseraft-web/deploy/restarts/), so restarting fuseraft-web
doesn't end them.
