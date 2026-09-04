# Arkade Heroes — Blazor WebAssembly frontend, served as static files by nginx.
#
# BUILD CONTEXT IS THE REPOSITORY ROOT, not this directory:
#
#     git submodule update --init external/dotnet-sdk     # ← REQUIRED (see the server Dockerfile)
#     docker build -f src/ArkadeHeroes.Web/Dockerfile -t arkade-heroes-web .
#
# SEPARATE ORIGIN BY DESIGN. The game server does NOT host this bundle; the two run as
# two containers on two ports, the browser calls the API cross-origin, and the server's
# CORS policy (Program.cs — SetIsOriginAllowed for localhost/127.0.0.1) already permits
# it. Do not "simplify" this by copying wwwroot into the server image: that would make
# the app same-origin and quietly retire a property the deployment depends on.

# ── Stage 1: publish the WASM bundle ────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ArkadeHeroes.Web sets <WasmBuildNative>true</WasmBuildNative> (Bit.Besql's in-browser
# SQLite needs a native relink), so the publish below needs the wasm-tools workload —
# without it the build fails asking for it. This is the slow layer; it is deliberately
# ahead of any COPY so it caches across source changes.
#
# Python is required too, and NOT optional: the native relink shells out to Emscripten's
# emcc, which is a Python script. The SDK image ships no Python, so without this the
# publish gets all the way to "Compiling native assets…" and then dies with
# "error : unable to find python in $PATH". CI never hits this because GitHub's
# ubuntu-latest runner has Python preinstalled — the container does not.
# emcc looks for `python`, not `python3`, hence the symlink.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && ln -sf /usr/bin/python3 /usr/bin/python \
 && rm -rf /var/lib/apt/lists/*

RUN dotnet workload install wasm-tools

# Project files first for a cacheable restore layer. Every src/ project file is copied,
# not just this project's direct references: ArkadeHeroes.Web → Client.Sdk → Chain, and an
# incomplete set makes `dotnet publish` fail late and cryptically with
# "NETSDK1004: Assets file … project.assets.json not found" rather than at restore.
# Copying all seven is cheap and survives someone adding a ProjectReference later.
COPY ArkadeHeroes.slnx ./
COPY src/ArkadeHeroes.Web/ArkadeHeroes.Web.csproj             src/ArkadeHeroes.Web/
COPY src/ArkadeHeroes.Client.Sdk/ArkadeHeroes.Client.Sdk.csproj src/ArkadeHeroes.Client.Sdk/
COPY src/ArkadeHeroes.Client/ArkadeHeroes.Client.csproj       src/ArkadeHeroes.Client/
COPY src/ArkadeHeroes.Chain/ArkadeHeroes.Chain.csproj         src/ArkadeHeroes.Chain/
COPY src/ArkadeHeroes.Server/ArkadeHeroes.Server.csproj       src/ArkadeHeroes.Server/
COPY src/ArkadeHeroes.Core/ArkadeHeroes.Core.csproj           src/ArkadeHeroes.Core/
COPY src/ArkadeHeroes.Shared/ArkadeHeroes.Shared.csproj       src/ArkadeHeroes.Shared/
COPY external/dotnet-sdk/Directory.Build.props external/dotnet-sdk/version.json external/dotnet-sdk/README.md external/dotnet-sdk/
COPY external/dotnet-sdk/NArk/NArk.csproj                             external/dotnet-sdk/NArk/
COPY external/dotnet-sdk/NArk.Core/NArk.Core.csproj                   external/dotnet-sdk/NArk.Core/
COPY external/dotnet-sdk/NArk.Abstractions/NArk.Abstractions.csproj   external/dotnet-sdk/NArk.Abstractions/
COPY external/dotnet-sdk/NArk.Arkade/NArk.Arkade.csproj               external/dotnet-sdk/NArk.Arkade/
COPY external/dotnet-sdk/NArk.Storage.EfCore/NArk.Storage.EfCore.csproj external/dotnet-sdk/NArk.Storage.EfCore/

RUN test -f external/dotnet-sdk/NArk.Core/NArk.Core.csproj || { \
      echo "ERROR: external/dotnet-sdk is empty — the NArk SDK submodule was not checked out."; \
      echo "Run:   git submodule update --init external/dotnet-sdk"; \
      echo "then re-run docker build from the repository root."; \
      exit 1; }

RUN dotnet restore src/ArkadeHeroes.Web/ArkadeHeroes.Web.csproj

COPY src/ src/
COPY external/dotnet-sdk/ external/dotnet-sdk/
# The Terms of Use, bundled into the app as wwwroot/terms.md by the csproj.
COPY docs/TERMS.md docs/TERMS.md

RUN dotnet publish src/ArkadeHeroes.Web/ArkadeHeroes.Web.csproj \
      --configuration Release \
      --no-restore \
      --output /app/publish

# ── Stage 2: static server ──────────────────────────────────────────────────
# nginx-unprivileged runs as uid 101 and listens on 8080 — no root, no setcap, no
# CAP_NET_BIND_SERVICE needed. Plain `nginx` would start its master process as root.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime

# Root only long enough to place files and fix ownership, then back to uid 101 for good.
USER root

COPY src/ArkadeHeroes.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html

# The API base URL is a RUNTIME setting, not a build-time one, so one published image can
# be pointed at regtest, signet or mainnet. ArkadeHeroes.Web/Program.cs reads
# builder.Configuration["ApiBaseUrl"], and WebAssemblyHostBuilder.CreateDefault fetches
# wwwroot/appsettings.json over HTTP to populate it. The repo ships no such file (the code
# falls back to http://localhost:5210), so this entrypoint WRITES one before nginx starts.
# No C# changes, no rebuild per environment.
COPY src/ArkadeHeroes.Web/docker-entrypoint.d/10-apibaseurl.sh /docker-entrypoint.d/10-apibaseurl.sh

# chown the DIRECTORY, not just the files in it. That script creates a NEW file in the web
# root, and creating a file needs write permission on the containing directory —
# /usr/share/nginx/html pre-exists in the base image owned by root, so copying files in as
# 101 is not enough. Getting this wrong makes the container crash-loop on
# "can't create …/appsettings.json: Permission denied".
RUN chmod +x /docker-entrypoint.d/10-apibaseurl.sh \
 && chown -R 101:101 /usr/share/nginx/html

USER 101

# Where the browser should reach the game API. Must be an origin the BROWSER can resolve
# (it is fetched from the user's machine, not from inside this container) — so it is the
# published host address, never http://server:8080.
ENV API_BASE_URL=http://localhost:5210

EXPOSE 8080
