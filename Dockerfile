# ── Stage 1: Build ──────────────────────────────────────────
# Root Dockerfile so Railway/Railpack builds the whole repo (build context = repo root).
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy csproj first for layer-cached restore
COPY PunchedApi/PunchedApi.csproj PunchedApi/
RUN dotnet restore PunchedApi/PunchedApi.csproj --runtime linux-musl-x64

# Copy everything else and publish
COPY PunchedApi/ PunchedApi/
RUN dotnet publish PunchedApi/PunchedApi.csproj \
    -c Release \
    --runtime linux-musl-x64 \
    --self-contained false \
    -o /app/publish \
    /p:UseAppHost=false

# ── Stage 2: Runtime ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Minimal Alpine hardening
RUN addgroup -S punched && adduser -S punched -G punched

COPY --from=build /app/publish .
RUN chown -R punched:punched /app

# Health check against the lightweight /health endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

# Non-root user
USER punched

EXPOSE 8080

# Railway injects PORT; fall back to 8080 locally / in docker compose
ENTRYPOINT ["sh", "-c", "export ASPNETCORE_URLS=http://+:${PORT:-8080} && exec dotnet PunchedApi.dll"]
