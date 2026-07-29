#!/bin/sh
# Point the published WASM bundle at a game API at RUNTIME.
#
# Run by nginx's own /docker-entrypoint.sh before the server starts (the base image
# executes every /docker-entrypoint.d/*.sh in name order).
#
# ArkadeHeroes.Web/Program.cs does:
#     var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5210";
# and the Blazor runtime fetches wwwroot/appsettings.json over HTTP at start-up to supply
# it. This script REWRITES that file, so one published image can be pointed at regtest,
# signet or mainnet. No C# changes; only the deployment differs.
#
# The file is committed at src/ArkadeHeroes.Web/wwwroot/appsettings.json and must stay
# there. The runtime only fetches config files registered in the PUBLISHED boot manifest,
# so a file merely dropped into the web root of an image built without one is never
# requested at all — verified: the browser made no request for it and silently used the
# compiled-in fallback. Overwriting a published file works; creating a new one does not.
#
# API_BASE_URL must be an origin the BROWSER can reach. It is fetched by the user's
# machine, so a compose service name (http://server:8080) resolves to nothing there.
set -eu

: "${API_BASE_URL:=http://localhost:5210}"
target=/usr/share/nginx/html/appsettings.json

if [ ! -f "$target" ]; then
  echo "10-apibaseurl.sh: FATAL: $target is missing from the published bundle." >&2
  echo "  It must be committed at src/ArkadeHeroes.Web/wwwroot/appsettings.json so the" >&2
  echo "  Blazor boot manifest registers it; otherwise the browser never fetches it and" >&2
  echo "  API_BASE_URL is silently ignored." >&2
  exit 1
fi

# Escape backslashes and double quotes so an odd URL cannot produce invalid JSON.
escaped=$(printf '%s' "$API_BASE_URL" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')

cat > "$target" <<JSON
{
  "ApiBaseUrl": "${escaped}"
}
JSON

# Drop the PRE-COMPRESSED siblings the Blazor publish generated next to this file.
#
# This is not tidiness, it is the whole fix. nginx.conf sets `gzip_static on`, so for any
# client that sends Accept-Encoding: gzip — every browser — nginx serves appsettings.json.gz
# in preference to appsettings.json. Rewriting only the plain file leaves the .gz holding
# the value baked in at publish time, so the browser silently keeps using the OLD API base
# while curl (which sends no Accept-Encoding by default) shows the new one. That mismatch
# is exactly what made this look like it worked when it did not.
#
# Deleting them is safe: gzip_static falls back to the uncompressed file, and this document
# is a few dozen bytes, so there is nothing to gain by compressing it.
rm -f "$target.gz" "$target.br"

echo "10-apibaseurl.sh: ApiBaseUrl set to ${API_BASE_URL}"
