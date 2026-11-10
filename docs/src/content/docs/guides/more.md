---
title: Profiles, schedule and more
description: Model profiles, defaults, scheduled jobs, plugins, context, configs and memory.
---

## Model profiles

A model profile stores a provider, a model ID, an endpoint, a reasoning effort and an API key once,
for every chat and session to use.

API keys go in the OS keychain: GNOME Keyring via `secret-tool`, the macOS Keychain or the Windows
Credential Manager. Without one, they go in a mode-600 file in your fuseraft home. A profile's key
reaches only the processes you start:

- as `FUSERAFT_PROFILE_{id}_KEY` for sessions, so `apiKeyEnvVar:` in a config resolves it;
- as `FUSERAFT_API_KEY` for chats.

## Defaults

The **Defaults** page edits your `fuseraft settings`: temperature and the other sampling defaults,
the memory and sub-agent models, chat limits, timeouts, telemetry and the rest, grouped and
searchable. Each one shows its description, its default and the range it takes. A change is saved,
and checked by fuseraft, when you leave the field; one fuseraft rejects shows why under it. Your next
chat uses it, while a running one keeps what it started with.

The model, provider and endpoint aren't here, because each chat takes them from its model profile,
and neither are the settings that only shape fuseraft in a terminal (theme, dock, bell and banner).
It needs fuseraft 1.23.0 or later.

## Schedule

The **Schedule** page adds, enables, disables and removes `fuseraft schedule` jobs: a name, a cron
expression, the task, and optionally a config path, a working directory and a description.

fuseraft-web runs every account's due jobs once a minute, so no cron entry is needed. Turn that off
with [`Fuseraft:RunSchedules`](/fuseraft-web/reference/config/). **Run** starts a job now as a live
session, with approvals, and leaves its next scheduled run as it is.

Each job's **Notify me of** choice says which of its runs you're [told about](/fuseraft-web/guides/notifications/#scheduled-jobs).

## Plugins

Every plugin fuseraft's agents can use, with its functions.

## Context

Reference documents attached to a workspace for agents to read: a file or directory, with a name and
a description. See the fuseraft [context store docs](/fuseraft-cli/context-store/).

## Configs

Scan a directory for orchestration configs and validate them with `fuseraft validate`.

## Memory

Browse and delete the memories your chats and agents keep. See the fuseraft
[knowledge docs](/fuseraft-cli/knowledge/).
