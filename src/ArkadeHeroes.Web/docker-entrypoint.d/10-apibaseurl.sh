#!/bin/sh
# Point the published WASM bundle at a game API at RUNTIME.
#
# Run by nginx's own /docker-entrypoint.sh before the server starts (the base image
# executes every /docker-entrypoint.d/*.sh in name order).
#
# ArkadeHeroes.Web/Program.cs does:
#     var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5210";
# and WebAssemblyHostBuilder.CreateDefault registers wwwroot/appsettings.json as an
# OPTIONAL config source fetched over HTTP at startup. The repo ships no such file, so
# writing one here is purely additive — it supplies the value the code already looks for
# instead of letting it fall back. Nothing in the app changes; only the deployment does.
#
# API_BASE_URL must be an origin the BROWSER can reach. It is fetched by the user's
# machine, so a compose service name (http://server:8080) resolves to nothing there.
set -eu

: "${API_BASE_URL:=http://localhost:5210}"
target=/usr/share/nginx/html/appsettings.json

# Escape backslashes and double quotes so an odd URL cannot produce invalid JSON.
escaped=$(printf '%s' "$API_BASE_URL" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')

cat > "$target" <<JSON
{
  "ApiBaseUrl": "${escaped}"
}
JSON

echo "10-apibaseurl.sh: ApiBaseUrl set to ${API_BASE_URL}"
