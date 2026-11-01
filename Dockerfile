# syntax=docker/dockerfile:1

# Memoria.Api only. The memoria-qmd sidecar is deliberately not in this image:
# it needs the GGUF models, the SQLite index and (today) a GPU-equipped Windows
# host, and runs as its own service there. See docs/deployment.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers cache independently of source changes.
COPY Directory.Build.props ./
COPY src/Memoria.Core/Memoria.Core.csproj src/Memoria.Core/
COPY src/Memoria.Api/Memoria.Api.csproj src/Memoria.Api/
RUN dotnet restore src/Memoria.Api/Memoria.Api.csproj

COPY src/ src/
RUN dotnet publish src/Memoria.Api/Memoria.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app ./

# The aspnet image defines a non-root user; run as it.
USER $APP_UID
EXPOSE 8080

# Liveness only: the API process is up. Readiness (the sidecar) is /ready.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "Memoria.Api.dll", "--health-check"]

ENTRYPOINT ["dotnet", "Memoria.Api.dll"]
