---
title: Chat
description: Chat with fuseraft in the browser, confined to a workspace, with approvals and saved chats.
---

The **Chat** page is the same engine as `fuseraft repl` and the VS Code extension, in a browser. Each
chat is a `fuseraft repl --vscode` process speaking the JSONL bridge.

## Start a chat

Pick a **Model Profile** and a **Workspace**, and optionally a **System Prompt**, then start the
chat. **Browse…** picks the workspace from the folders you may use. If the folder you typed doesn't
exist yet, the chat offers to create it.

Type a message and press **Enter** to send it, or **Shift+Enter** for a new line. A new chat lists
what you can type and a few prompts to start from, and **Continue** resumes your last chat in the
same workspace.

## Replies

Replies stream in and render as Markdown, with a **Copy** button on each code block. Each tool call
shows as a card with its result and how long it took; open it for the arguments and the full output.
A model that exposes its reasoning shows it under **Thinking**. Below a reply are the files it
changed, which show their diff when clicked, and the tokens it used. The bar above the chat shows how
full the context window is; hover it for the chat's token totals.

**Changes** lists every file the chat has changed, and **Export** downloads the chat as Markdown.
A diff is against the last commit, so it includes anything else not yet committed; it needs the
workspace to be a git repository.

## Typing

- `/` opens a menu of slash commands with what each does (fuseraft v1.34.0 or later). `/help` lists
  them all.
- `@` suggests files and folders in the workspace. A file you mention goes along with your message.
- `!command` runs a shell command in the workspace yourself, such as `!git status`, and shows its
  output. It isn't sent to the model.
- **Attach**, or paste or drop images into the box, to send up to four images (5 MB each) to a model
  that can see them.

The model's name above the chat switches the chat to another model from the profile's provider
(`/model`), keeping the profile's key and endpoint.

With fuseraft v1.20.0 or later, you can keep typing while a reply streams. A message reaches the
model at its next call, or starts the next turn if the reply finishes first; it's marked **queued**
until then, and **Stop** drops it.
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

## Tools

With fuseraft v1.27.0 or later, the **Tools** button above the chat shows how many tools the model
is offered. It opens a switch for each tool category and each tool in it, for this chat only.
**Save as default** makes new chats start with the same tools, and **Reset** goes back to your
defaults. Tools can't be changed while a reply streams, and safe mode keeps Shell, Git and Http off
until `/safe-mode off`. The defaults are also on the [Defaults](/fuseraft-web/guides/more/#defaults)
page.

## Slash commands

Slash commands such as `/model` or `/compact` go to the CLI, which handles them as it does in the
terminal. See the fuseraft [REPL docs](/fuseraft-cli/repl/) for more. `/copy` puts the last reply on
your clipboard.

**Clear** (or `/clear`) starts a new chat in the same workspace. With fuseraft v1.27.0 or later, the
chat you leave is saved and stays in **Recent Chats**.

## Saved chats

A chat is saved to your fuseraft home after each turn. **Recent Chats** on the Chat page lists
them, and opening one resumes it with its last few turns shown again. Your chats are your own:
admins can't see them.

Open chats run in [relay processes](/fuseraft-web/deploy/restarts/), so restarting fuseraft-web
doesn't end them.
