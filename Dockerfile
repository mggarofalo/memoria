# syntax=docker/dockerfile:1

# Memoria.Api only. The memoria-qmd sidecar is deliberately not in this image:
# it needs the GGUF models, the SQLite index and a GPU-equipped Windows
# host, and runs as its own service there. See docs/deployment.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers cache independently of source changes.
COPY Directory.Build.props ./
COPY src/Memoria.Core/Memoria.Core.csproj src/Memoria.Core/
COPY src/Memoria.Api/Memoria.Api.csproj src/Memoria.Api/
RUN dotnet restore src/Memoria.Api/Memoria.Api.csproj

COPY src/ src/

# The version comes from the release tag (.github/workflows/docker.yml); .git is
# not in the build context, so an image built without it reports 0.0.0-dev.
ARG MEMORIA_VERSION=0.0.0-dev
RUN dotnet publish src/Memoria.Api/Memoria.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    -p:UseAppHost=false \
    -p:Version=$MEMORIA_VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Memoria__Auth__DatabasePath=/data/control.db

COPY --from=build /app ./

# The control database (API keys, audit log). A named volume mounted here takes
# this directory's ownership on first use, so the non-root user can write to it.
RUN mkdir -p /data && chown "$APP_UID" /data

# The aspnet image defines a non-root user; run as it.
USER $APP_UID
EXPOSE 8080

# Liveness only: the API process is up. Readiness (the sidecar) is /ready.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "Memoria.Api.dll", "--health-check"]

ENTRYPOINT ["dotnet", "Memoria.Api.dll"]
