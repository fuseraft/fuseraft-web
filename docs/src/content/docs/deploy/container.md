---
title: Run it in a container
description: The container image, what it holds, where its data lives, and what a container changes about restarts, workspaces and API keys.
---

Each release is also published as a container image for linux/amd64 and linux/arm64:

```bash
docker run -d --name fuseraft-web \
  -p 127.0.0.1:5000:5000 \
  -v fuseraft-data:/data \
  ghcr.io/fuseraft/fuseraft-web:latest
docker logs fuseraft-web 2>&1 | grep setup   # the first admin's setup link
```

Or create the first admin from a shell:

```bash
docker exec -it fuseraft-web fuseraft-web users add alice --admin
```

The repository has a [`compose.yaml`](https://github.com/fuseraft/fuseraft-web/blob/main/compose.yaml)
with the same settings and commented-out options. Publishing on `127.0.0.1` keeps the server
unreachable from other machines until a [TLS reverse proxy](/fuseraft-web/deploy/reverse-proxy/)
is in front of it. Inside the container it listens on port 5000 on every interface, so it logs a
plain-HTTP warning at startup.

:::caution
A container keeps agents away from the host, but not away from each other. Every account in one
container still runs as the same user (see the [trust model](/fuseraft-web/guides/accounts/#users-and-admins)).
For people who don't trust each other, run one container each.
:::

## What's in the image

- `fuseraft-web`, in `/opt/fuseraft-web`.
- The `fuseraft` CLI that was latest when the image was built. Its version is in the image's
  `ai.fuseraft.cli.version` label.
- `git`, and `tini` as process 1, so processes agents leave behind are reaped.

It has no compilers, runtimes or test tools. Agents can read and edit code, but can't build or
test it until you add what your projects need:

```dockerfile
FROM ghcr.io/fuseraft/fuseraft-web:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends nodejs npm python3 \
 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
```

To build the image from source: `docker build -t fuseraft-web .` in a clone. Pass
`--build-arg FUSERAFT_CLI_VERSION=<version>` to bundle a different CLI release.

## Data and workspaces

The image sets `HOME=/data`, so everything to keep is in one volume:

| Path | |
|---|---|
| `/data/.fuseraft-web` | The [data directory](/fuseraft-web/reference/config/): accounts, keys, saved daemons, later accounts' homes and default workspaces |
| `/data/.fuseraft` | The first account's fuseraft home |

Back that volume up as described in [What to back up](/fuseraft-web/deploy/backups/).

Agents can only work on what's in the container. Bind-mount your repositories, for example
`-v /srv/repos:/workspace`, and add `/workspace` to people's workspace roots on the Users page.
The files must be writable by the container's user, UID 1654 by default. To run as the
repositories' owner instead, pass `--user 1000:1000` and bind-mount `/data` from a directory that
user owns too. A new named volume belongs to UID 1654.

## Restarts end running work

Outside a container, sessions and chats [outlive a server restart](/fuseraft-web/deploy/restarts/).
In a container, their relays run inside it. Stopping, restarting or upgrading the container ends
them all, just like a reboot. Their logs and snapshots are in `/data`, so you can resume a session
from the Sessions page and a chat from Recent Chats on the Chat page. Upgrade when nothing important
is running.

## API keys

The image has no keychain, so the API keys in model profiles go in mode-600 files in each account's
fuseraft home (`profile-keys/`) under `/data`. Anyone who can read the volume can read them, so
keep its backups as private as the keys.

## The CodeExecution plugin

CodeExecution runs code in Docker containers, and the image has no Docker. Don't mount the host's
Docker socket to make it work: whoever controls that socket controls the host, and agents would too.
