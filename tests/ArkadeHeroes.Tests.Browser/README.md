# ArkadeHeroes.Tests.Browser — published-bundle smoke (Playwright)

Publishes `src/ArkadeHeroes.Web`, serves the resulting bundle, and drives it in a real headless
Chromium.

This is the only gate in the repo that sees **what a player actually downloads**. A build output and a
Release publish are different artifacts: the published one is IL-linked, fingerprinted, brotli-compressed
and started through a boot manifest.

Defects that live there are total — a blank page — and one has shipped: the `TypeInitializationException`
that took out every page touching the content pack. It was diagnosed as a trimming problem and `#207`
later established it was not, it was a static-initializer cycle in `ContentPackVersion`. Both readings
share the thing that matters here: **a local build and the whole unit suite stayed green the entire
time**, because a static initializer that throws on startup is invisible to anything that never starts
the app. That is what this suite is for.

It also unblocks a specific decision. `PublishTrimmed` is still `false`, and the csproj now says it is
kept off only until someone takes a trimmed bundle "through a funded flow… with a browser pass over the
money paths". This suite is the browser pass — it does not drive money paths yet (see below), but it is
where that work belongs.

## Run it

```sh
# One-off: fetch the browser binary.
pwsh tests/ArkadeHeroes.Tests.Browser/bin/Release/net10.0/playwright.ps1 install chromium

# Publishes on demand (a native emscripten link — expect minutes on a cold run).
dotnet test tests/ArkadeHeroes.Tests.Browser -c Release
```

To reuse a bundle you have already published — which is what CI does, and what you want when iterating:

```sh
dotnet publish src/ArkadeHeroes.Web -c Release -o /tmp/ahweb
ARKADE_WEB_PUBLISH_DIR=/tmp/ahweb dotnet test tests/ArkadeHeroes.Tests.Browser -c Release
```

The suite serves the bundle itself on **port 5198** from an in-process Kestrel host, so there is no
external web server to install or leave running.

## What it covers

- The published bundle **boots** — the pre-boot "inserting coin" screen is replaced by real app content,
  which only happens if the runtime started and the root component ran.
- **No unhandled error at startup**: Blazor's `#blazor-error-ui` banner is not visible, and no
  `TypeInitializationException` reaches the console — the exact end state of the content-pack crash.
- **Content-backed routes render** in the published bundle — `/play`, `/gauntlet`, `/codex`, `/heroes`,
  `/wallet`. `/gauntlet` resolves `ContentPack.Default.FindDungeon("gauntlet")` in a static initializer,
  which is the initializer that actually went down.
- The bundle **still boots with every backend unreachable** (arkd, esplora and the game API all aborted
  at the network layer). Worth pinning for its own sake — a player on a flaky connection should get a
  page that says something rather than a permanent loading screen — and it is also what lets this suite
  run with no infrastructure at all.

## If every test suddenly fails to boot

Check for a stale publish cache before suspecting the app. Publishing once with different linker
settings — `dotnet publish -p:PublishTrimmed=true`, say — leaves a trimmed `System.Private.CoreLib` in
`src/ArkadeHeroes.Web/obj/`, and the next ordinary publish pairs it with an **un**trimmed native runtime.
The browser then says:

```
Your mono runtime and class libraries are out of sync.
The out of sync library is: System.Private.CoreLib.dll
```

and the page never leaves "inserting coin". Clearing `obj/Release/net10.0/wasm` alone does **not** fix it;
remove `src/ArkadeHeroes.Web/obj` and `bin` entirely and publish again. A quick way to tell a real failure
from this one: compare `System.Private.CoreLib.*.wasm` in the published `_framework/` against a known-good
bundle — a trimmed one is roughly half the size.

## Why it is not on the PR gate

Not because the infrastructure is missing — it needs none. The publish is a native emscripten link that
takes minutes, which is the wrong tax on every push. So it runs **nightly and on manual dispatch**,
matching the existing `e2e-test` job. That job's own comment records why: three E2E tests sat red for
weeks when they ran on manual dispatch alone.

Fast render coverage that *does* gate every PR lives in `tests/ArkadeHeroes.Tests.Web`.

## What it deliberately does NOT cover

Kept deliberately small, because a slow suite that nobody trusts is worse than no suite.

- **No game flows.** Nothing here signs in, recruits, breeds, duels or spends. It asserts that the app
  starts and its pages draw, not that they do the right thing — that is
  `tests/ArkadeHeroes.Tests.Web` (render logic) and `tests/ArkadeHeroes.Tests.E2E` (real covenants).
- **No wallet, no keys, no chain.** The backends are not required and are not driven.
- **No visual or layout assertions.** No screenshots, no pixel diffs. An ugly page passes.
- **One browser.** Chromium only. No Firefox or WebKit, and no mobile viewports.
- **Not the production host.** It serves the bundle from Kestrel with a SPA fallback; it does not test
  the real container entrypoint, its `appsettings.json` rewriting, caching headers or compression
  negotiation.
