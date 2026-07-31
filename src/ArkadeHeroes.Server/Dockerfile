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

# ── The Blazor bundle, into this image's wwwroot ────────────────────────────
# ONE container is the whole deployment: the server hosts the frontend, so the browser
# loads the app and calls /api on the SAME origin. That is what makes the localhost-only
# CORS policy a non-issue rather than something to widen for a public domain.
#
# wasm-tools relinks the .NET runtime for the browser and is by far the slowest step here,
# so it sits in its own layer. emcc looks for `python`, not `python3`, hence the symlink —
# the same recipe the standalone web image used, which this replaces.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && ln -sf /usr/bin/python3 /usr/bin/python \
 && rm -rf /var/lib/apt/lists/* \
 && dotnet workload install wasm-tools

# The Terms of Use, bundled into the app as wwwroot/terms.md by the csproj.
COPY docs/TERMS.md docs/TERMS.md

# No --no-restore: installing the wasm-tools workload changes what restore must resolve, so
# the restore done above (before the workload existed) does not cover this project.
RUN dotnet publish src/ArkadeHeroes.Web/ArkadeHeroes.Web.csproj \
      --configuration Release \
      --output /app/web

# A Blazor WASM publish puts everything the browser needs under wwwroot/. Copying it into
# the server's wwwroot is all the "hosting" that is needed: the server already serves static
# files and rewrites a non-/api 404 to index.html.
RUN mkdir -p /app/publish/wwwroot && cp -r /app/web/wwwroot/. /app/publish/wwwroot/

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

# Durability is opt-in in the app (no key ⇒ everything in memory, lost on restart), and that
# default is right for `dotnet run` but wrong for anything shipped. Defaulted HERE rather than
# only in docker-compose.yml because a platform that deploys this IMAGE directly — Dokploy, a
# bare `docker run` — never reads that file, and the failure is silent: the server boots clean
# and serves happily while writing nothing, so the roster only disappears at the next redeploy.
# An operator can still override it, and compose sets the identical value explicitly.
#
# NOTE: this makes the DB get WRITTEN; keeping it needs /data backed by a NAMED volume or host
# mount. The bare VOLUME above is anonymous — it survives a restart but is orphaned when the
# container is recreated, which is exactly what a redeploy does.
ENV Game__StateDbPath=/data/game.db

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish ./

# Copied from the source tree, not the build output — it is a deployment script, not a
# compiled artifact. chmod because a checkout on Windows carries no execute bit.
COPY --chown=$APP_UID:$APP_UID src/ArkadeHeroes.Server/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh

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
# Injects ARK_NETWORK into the bundled frontend's appsettings.json, then execs the server.
# `exec` matters: the server must be PID 1 so a container stop reaches it as SIGTERM rather
# than being swallowed by a shell that never forwards it.
ENTRYPOINT ["/app/docker-entrypoint.sh"]
