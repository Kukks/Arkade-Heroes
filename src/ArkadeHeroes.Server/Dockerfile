# Arkade Heroes — game API server.
#
# BUILD CONTEXT IS THE REPOSITORY ROOT, not this directory:
#
#     git submodule update --init external/dotnet-sdk     # ← REQUIRED, see below
#     docker build -f src/ArkadeHeroes.Server/Dockerfile -t arkade-heroes-server .
#
# THE SUBMODULE MATTERS. ArkadeHeroes.Chain has ProjectReferences into
# external/dotnet-sdk (the NArk SDK, a git submodule). A plain `git clone` leaves that
# directory EMPTY, and the restore then dies with the famously opaque:
#
#     error MSB3202: The project file "…/NArk/NArk.csproj" was not found.
#
# There is no way for a Dockerfile to fetch a submodule for you — the build context is
# whatever the daemon was handed, and the daemon has no git. So this image FAILS FAST
# with a readable message instead (see the guard after the csproj copy below).

# ── Stage 1: restore + publish ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so the restore layer is cached until a .csproj actually changes.
# All seven are copied rather than just this project's closure — an incomplete set makes
# `dotnet publish` fail late with "NETSDK1004: Assets file … not found" instead of at
# restore, and it survives someone adding a ProjectReference later. Only the SERVER
# project is restored and published below, so ArkadeHeroes.Web is never built here and
# this image needs no wasm-tools workload (the frontend image installs it).
COPY ArkadeHeroes.slnx ./
COPY src/ArkadeHeroes.Server/ArkadeHeroes.Server.csproj   src/ArkadeHeroes.Server/
COPY src/ArkadeHeroes.Core/ArkadeHeroes.Core.csproj       src/ArkadeHeroes.Core/
COPY src/ArkadeHeroes.Shared/ArkadeHeroes.Shared.csproj   src/ArkadeHeroes.Shared/
COPY src/ArkadeHeroes.Chain/ArkadeHeroes.Chain.csproj     src/ArkadeHeroes.Chain/
COPY src/ArkadeHeroes.Client/ArkadeHeroes.Client.csproj   src/ArkadeHeroes.Client/
COPY src/ArkadeHeroes.Client.Sdk/ArkadeHeroes.Client.Sdk.csproj src/ArkadeHeroes.Client.Sdk/
COPY src/ArkadeHeroes.Web/ArkadeHeroes.Web.csproj         src/ArkadeHeroes.Web/
# The SDK submodule's own MSBuild props + version.json apply to every NArk project.
COPY external/dotnet-sdk/Directory.Build.props external/dotnet-sdk/version.json external/dotnet-sdk/README.md external/dotnet-sdk/
COPY external/dotnet-sdk/NArk/NArk.csproj                             external/dotnet-sdk/NArk/
COPY external/dotnet-sdk/NArk.Core/NArk.Core.csproj                   external/dotnet-sdk/NArk.Core/
COPY external/dotnet-sdk/NArk.Abstractions/NArk.Abstractions.csproj   external/dotnet-sdk/NArk.Abstractions/
COPY external/dotnet-sdk/NArk.Swaps/NArk.Swaps.csproj                 external/dotnet-sdk/NArk.Swaps/
COPY external/dotnet-sdk/NArk.Storage.EfCore/NArk.Storage.EfCore.csproj external/dotnet-sdk/NArk.Storage.EfCore/

# Fail fast and legibly if the submodule was never initialised. The COPY above would
# already have failed, but only with "file not found" naming a path nobody recognises;
# this turns it into an instruction. (Kept as a guard for the case where a stale/partial
# submodule checkout leaves the directory present but the project file missing.)
RUN test -f external/dotnet-sdk/NArk/NArk.csproj || { \
      echo "ERROR: external/dotnet-sdk is empty — the NArk SDK submodule was not checked out."; \
      echo "Run:   git submodule update --init external/dotnet-sdk"; \
      echo "then re-run docker build from the repository root."; \
      exit 1; }

RUN dotnet restore src/ArkadeHeroes.Server/ArkadeHeroes.Server.csproj

# Now the sources.
COPY src/ src/
COPY external/dotnet-sdk/ external/dotnet-sdk/

RUN dotnet publish src/ArkadeHeroes.Server/ArkadeHeroes.Server.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish

# ── Stage 2: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl, purely so the compose healthcheck can probe /api/chain/info. The .NET runtime
# images ship neither curl nor wget (verified), and a HEALTHCHECK whose binary is missing
# does not warn — it just reports "unhealthy" forever, which is worse than having none.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# /data is where the durable SQLite state DB lives (Game__StateDbPath). It is a VOLUME
# in docker-compose.yml — without that, every container restart destroys the player and
# hero records. Created and chowned here so the non-root user can actually write it.
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME ["/data"]

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./

# Non-root. The .NET 8+ runtime images ship a pre-created `app` user (uid/gid 1654)
# and expose it as $APP_UID; use it rather than inventing another one.
USER $APP_UID

# Bind to all interfaces on 8080 — a container that binds localhost is unreachable from
# outside it. NOTE: this is the UNPREFIXED `Urls` key on purpose, not ASPNETCORE_URLS.
# appsettings.json ships "Urls": "http://localhost:5210", and in the WebApplicationBuilder
# configuration chain the ASPNETCORE_-prefixed provider is added BEFORE appsettings.json —
# so ASPNETCORE_URLS LOSES to that file, while the unprefixed environment provider is
# added after it and wins. docker-compose.yml sets ASPNETCORE_URLS too (it is the key
# operators expect), and it is honoured because it resolves to the same address.
ENV Urls=http://0.0.0.0:8080 \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

# /healthz is a plain liveness probe; docker-compose.yml health-checks /api/chain/info,
# which additionally proves the chain service resolved and the game config is serving.
ENTRYPOINT ["dotnet", "ArkadeHeroes.Server.dll"]
