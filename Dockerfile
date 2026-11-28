# fuseraft-web container image. See docs/src/content/docs/deploy/container.md.
#
#   docker build -t fuseraft-web .
#   docker build --build-arg FUSERAFT_CLI_VERSION=1.33.0 -t fuseraft-web .
#
# The base is deliberately small: git and the fuseraft CLI, no language toolchains. Extend it
# with what your agents build and test with (FROM ghcr.io/fuseraft/fuseraft-web, then apt-get).

# The build stage runs on the build machine's architecture and cross-publishes for the target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# MinVer reads the version from git tags; CI passes the tag's version here instead.
ARG VERSION=
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && dotnet publish FuseraftWeb.csproj -c Release -r "$rid" --self-contained -o /out --nologo \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
      ${VERSION:+-p:MinVerVersionOverride=$VERSION}

# Fetched on the build machine too; only the final stage runs on the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS cli
ARG TARGETARCH
ARG FUSERAFT_CLI_VERSION=1.33.0
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; esac \
 && curl -fsSL --retry 3 -o /tmp/cli.tar.gz \
      "https://github.com/fuseraft/fuseraft-cli/releases/download/v${FUSERAFT_CLI_VERSION}/fuseraft-${FUSERAFT_CLI_VERSION}-${rid}.tar.gz" \
 && tar -xzf /tmp/cli.tar.gz -C /usr/local/bin fuseraft

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG FUSERAFT_CLI_VERSION=1.33.0
LABEL org.opencontainers.image.source="https://github.com/fuseraft/fuseraft-web" \
      org.opencontainers.image.description="fuseraft in the browser, shared by a team" \
      org.opencontainers.image.licenses="MIT" \
      ai.fuseraft.cli.version="${FUSERAFT_CLI_VERSION}"

# tini reaps the processes agents leave behind; git is what nearly every agent task needs.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git tini \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data && chown "$APP_UID:$APP_UID" /data && chmod 700 /data

COPY --from=cli /usr/local/bin/fuseraft /usr/local/bin/fuseraft
COPY --from=build /out/ /opt/fuseraft-web/

# Everything to keep lives under HOME, so one volume holds it whatever --user runs the image:
# the data directory (/data/.fuseraft-web), the first account's fuseraft home (/data/.fuseraft)
# and new accounts' workspaces.
ENV HOME=/data \
    ASPNETCORE_HTTP_PORTS=5000 \
    DOTNET_NOLOGO=1 \
    PATH=/opt/fuseraft-web:$PATH
VOLUME /data
WORKDIR /opt/fuseraft-web
# The base image's non-root app user (UID 1654). Run with --user to match a bind mount's owner.
USER $APP_UID
EXPOSE 5000
ENTRYPOINT ["/usr/bin/tini", "--", "fuseraft-web"]
