# syntax=docker/dockerfile:1.28.0@sha256:bb22d9815c728170f72750f4e5b0d672e06176142e1d602c7e66c050100b7e5b
#
# Multi-stage Containerfile for the nutanix-mcp server image.
#
#   Stage 1 (build) — full SDK on Ubuntu, restores + publishes a
#                     self-contained single-file binary for $TARGETARCH.
#                     Self-contained because the runtime stage is
#                     `runtime-deps` (no managed runtime baked in).
#
#   Stage 2 (runtime) — `runtime-deps:10.0-resolute-chiseled-extra`.
#                       Chiseled = no shell, no package manager, ~100 MB
#                       smaller, fewer CVEs; the `-extra` variant keeps
#                       ICU + tzdata which the corpus pipeline needs for
#                       Russian/UTF-8 string handling. Ships a non-root
#                       `app` user (UID 1654) by default.
#
# Multi-arch: docker buildx populates TARGETARCH (`amd64` / `arm64`);
# we map that to the .NET RID once and reuse it for `dotnet publish`.

# Base images pinned by digest (Ubuntu 26.04 "resolute"); bump both together.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-resolute@sha256:6924c5c6564690089a67509f5a6ae85d7afa28d7f0f6466ec1b8a5439764ca92
ARG RUNTIME_IMAGE_NAME=mcr.microsoft.com/dotnet/runtime-deps:10.0.13-resolute-chiseled-extra
ARG RUNTIME_IMAGE_DIGEST=sha256:8d3382bf11c0da9d087b1852dc2859c5ff113348cbab20fa1282babf36b2386a

# ──────────────────────────────────────────────────────────────────────
# Stage 1 — build
# ──────────────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
ARG TARGETARCH
ARG VERSION=0.0.0
ARG COMMIT=unknown

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src

# Copy only the dependency manifests first so the restore layer caches
# across source-only changes.
COPY global.json Directory.Build.props Directory.Packages.props nutanix-mcp.slnx ./
COPY src/ ./src/
COPY BannedSymbols.txt ./

# Map docker arch → .NET RID.
RUN case "${TARGETARCH}" in \
        amd64) echo "linux-x64"   > /tmp/rid ;; \
        arm64) echo "linux-arm64" > /tmp/rid ;; \
        *) echo "unsupported TARGETARCH=${TARGETARCH}" >&2; exit 1 ;; \
    esac

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /tmp/rid)" \
    && dotnet restore src/NutanixMcp.Cli/NutanixMcp.Cli.csproj \
        --runtime "$RID"

RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked \
    RID="$(cat /tmp/rid)" \
    && dotnet publish src/NutanixMcp.Cli/NutanixMcp.Cli.csproj \
        --configuration Release \
        --runtime "$RID" \
        --self-contained true \
        --no-restore \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:PublishTrimmed=false \
        -p:DebugType=embedded \
        -p:Version=${VERSION} \
        -p:InformationalVersion=${VERSION}+${COMMIT} \
        --output /app/publish

# ──────────────────────────────────────────────────────────────────────
# Stage 2 — runtime
# ──────────────────────────────────────────────────────────────────────
FROM ${RUNTIME_IMAGE_NAME}@${RUNTIME_IMAGE_DIGEST} AS runtime

ARG VERSION=0.0.0
ARG COMMIT=unknown
ARG RUNTIME_IMAGE_NAME
ARG RUNTIME_IMAGE_DIGEST

LABEL org.opencontainers.image.title="nutanix-mcp" \
      org.opencontainers.image.description="Hybrid retrieval MCP server for Nutanix documentation" \
      org.opencontainers.image.source="https://github.com/ioplane/nutanix-mcp" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${COMMIT}" \
      org.opencontainers.image.base.name="${RUNTIME_IMAGE_NAME}" \
      org.opencontainers.image.base.digest="${RUNTIME_IMAGE_DIGEST}"

WORKDIR /app
COPY --from=build --chown=app:app /app/publish/ /app/

# Models are mounted at runtime via the Quadlet volume — pre-create the
# mountpoint so the read-only rootfs doesn't reject a fresh bind.
USER app
ENV NUTANIX_MCP__MODELSDIR=/var/lib/nutanix-mcp/models \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

EXPOSE 7456

# `serve --transport http --bind 0.0.0.0 --port 7456` — bind 0.0.0.0
# inside the container so Podman's PublishPort can route host traffic
# in. The host side stays loopback-only via PublishPort=127.0.0.1:7456.
ENTRYPOINT ["/app/nutanix-mcp", "serve", "--transport", "http", "--bind", "0.0.0.0", "--port", "7456"]
