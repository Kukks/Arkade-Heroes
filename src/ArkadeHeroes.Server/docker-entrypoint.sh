#!/bin/sh
# Point the bundled WASM frontend at its Ark network at RUNTIME, then start the server.
#
# The frontend is served from this same container now, so the nginx entrypoint that used to
# do this is gone — but the need is not. The in-browser wallet talks to arkd DIRECTLY, and
# which network it dials is a deployment fact baked into no image: one published image has
# to serve regtest, mutinynet and mainnet.
#
# ArkadeHeroes.Web/Program.cs reads ArkNetwork out of wwwroot/appsettings.json, which the
# Blazor runtime fetches over HTTP at start-up. Rewriting that file is the whole mechanism.
#
# ApiBaseUrl is deliberately NOT written: empty means same-origin, and same-origin is now
# the normal case — whoever served the app also serves /api. Setting it would be a way to
# get it wrong.
set -eu

: "${ARK_NETWORK:=regtest}"
target=/app/wwwroot/appsettings.json

# Reject an unknown network HERE rather than shipping a bundle that throws in the browser
# after the user has loaded the app. Program.cs refuses it too; this is the copy that fails
# while there is still a container log to read it in. A wallet silently on the wrong network
# puts player funds somewhere they cannot reach, so refusing to start is the kind outcome.
case "$ARK_NETWORK" in
  regtest|mutinynet|mainnet) ;;
  *)
    echo "entrypoint: FATAL: ARK_NETWORK='$ARK_NETWORK' is not a known network." >&2
    echo "  Use regtest, mutinynet or mainnet. Refusing to start rather than defaulting." >&2
    exit 1
    ;;
esac

if [ -f "$target" ]; then
  cat > "$target" <<JSON
{
  "ArkNetwork": "${ARK_NETWORK}"
}
JSON
  # Drop the PRE-COMPRESSED siblings the Blazor publish generated next to this file. With
  # static-file compression serving .gz in preference to the plain file, rewriting only the
  # plain one leaves the browser reading the value baked in at publish time — while curl,
  # which sends no Accept-Encoding by default, shows the new one. That mismatch is exactly
  # what once made this look like it worked when it did not.
  rm -f "$target.gz" "$target.br"
  echo "entrypoint: ArkNetwork=${ARK_NETWORK}"
else
  # An API-only image (no bundle) is a legitimate way to run this; say so rather than
  # failing, but say it, so a missing frontend is never a silent surprise.
  echo "entrypoint: no wwwroot/appsettings.json — running API-only, ARK_NETWORK ignored." >&2
fi

exec dotnet ArkadeHeroes.Server.dll
