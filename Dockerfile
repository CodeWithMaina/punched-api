# syntax=docker/dockerfile:1.7
# ═════════════════════════════════════════════════════════════════════════════
#  Punched API + Worker — multi-stage production image
#
#  One image serves both roles; the role is chosen at runtime:
#     PUNCHED_ROLE=api     → HTTP API,     WORKERS_ENABLED=false
#     PUNCHED_ROLE=worker  → hosted services only, WORKERS_ENABLED=true
#  so the two containers can never drift apart in code or configuration.
#
#  Image tags are pinned to the exact patch release (verified against the
#  registries) — re-pin deliberately, never float on `8.0-alpine`. For a fully
#  reproducible build pin by digest in CI:
#     --build-arg DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet@sha256:…
# ═════════════════════════════════════════════════════════════════════════════
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0.428-alpine3.24
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0.34-alpine3.24

# ── Stage 1: restore + publish ───────────────────────────────────────────────
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

# Restore is its own layer: application code changes do not invalidate it.
COPY PunchedApi.csproj ./
RUN dotnet restore PunchedApi.csproj --runtime linux-musl-x64

COPY . ./
RUN dotnet publish PunchedApi.csproj \
        -c Release \
        --runtime linux-musl-x64 \
        --self-contained false \
        --no-restore \
        -o /app/publish \
        /p:UseAppHost=false \
        /p:DebugType=None \
        /p:DebugSymbols=false

# ── Stage 2: runtime ─────────────────────────────────────────────────────────
FROM ${DOTNET_RUNTIME_IMAGE} AS runtime

# tzdata          — TimeZoneInfo.FindSystemTimeZoneById("Africa/Nairobi") drives
#                   attendance day boundaries and notification quiet hours;
#                   Alpine ships no zone database, so without this every lookup
#                   silently falls back to UTC.
# icu-libs        — culture-aware formatting (invariant mode forced off below).
# fontconfig /
# freetype        — font discovery for SkiaSharp card rendering.
# ca-certificates — TLS verification for Neon, R2, SMTP, Africa's Talking and
#                   the LLM providers.
RUN apk add --no-cache tzdata icu-libs fontconfig freetype ca-certificates \
    && addgroup -g 10001 -S punched \
    && adduser -u 10001 -S punched -G punched \
    && mkdir -p /app /keys \
    && chown -R 10001:10001 /app /keys

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    TZ=Africa/Nairobi

COPY --from=build --chown=10001:10001 /app/publish ./

# Dropped capabilities, read-only root filesystem and no-new-privileges are set
# by docker-compose.yml; the image itself never runs as root.
USER 10001:10001

EXPOSE 8080

# Liveness only: no dependency round trip, so a slow Neon connection can never
# make Docker restart a healthy process.
HEALTHCHECK --interval=20s --timeout=5s --start-period=30s --retries=3 \
    CMD wget --spider -q http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "PunchedApi.dll"]
