# ArkadeHeroes.Tests.Browser — the game in a real browser (Playwright)

Publishes `src/ArkadeHeroes.Web` and drives the resulting bundle in headless Chromium. Two fixtures, two
different questions:

| Fixture | Serves the bundle from | Backends | Question |
| --- | --- | --- | --- |
| `PublishedAppFixture` | a bare static-file Kestrel host | none — every one aborted | **does it start?** |
| `PlayableAppFixture` | the real `ArkadeHeroes.Server` | the real game API on the in-memory chain | **does it work?** |

This is the only gate in the repo that sees **what a player actually downloads**. A build output and a
Release publish are different artifacts: the published one is IL-linked, fingerprinted, brotli-compressed
and started through a boot manifest.

Defects that live there are total — a blank page — and one has shipped: the `TypeInitializationException`
that took out every page touching the content pack. It was diagnosed as a trimming problem and `#207`
later established it was not, it was a static-initializer cycle in `ContentPackVersion`. Both readings
share the thing that matters here: **a local build and the whole unit suite stayed green the entire
time**, because a static initializer that throws on startup is invisible to anything that never starts
the app. That is what this suite is for.

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

Both fixtures read that one variable. Neither needs Docker, arkd, bitcoind or the regtest stack; the game
server runs in-process on the in-memory chain, on a port the OS picks.

**Republish after touching a `.razor` file.** The suite tests the bundle, not your working tree, so an
edit you have not published is an edit the browser cannot see — and the run will look like it disagrees
with the code in front of you.

## What it covers

### Does it start (`WasmStartupTests`)

- The published bundle **boots** — the pre-boot "inserting coin" screen is replaced by real app content.
- **No unhandled error at startup**, and no `TypeInitializationException` in the console.
- **Content-backed routes render** — `/play`, `/gauntlet`, `/codex`, `/heroes`, `/wallet`.
- The bundle **still boots with every backend unreachable**. Worth pinning for its own sake — a player on
  a flaky connection should get a page that says something rather than a permanent loading screen.

### Does it work (`ArenaWalkTests`, `SeededArenaTests`, `HonestPageTests`, `NewPlayerWalkTests`)

The server here is `ArkadeHeroes.Server`'s own `Program`, on Kestrel, with the published bundle as its
wwwroot — **the shipped topology**, one origin serving both the app and `/api`, exactly as the container
does. `PlayableAppFixture` reproduces the container entrypoint's `appsettings.json` rewrite to get there,
pre-compressed siblings and all.

- **Every route renders without throwing.** All 27 routable pages, each asserted on three axes a 200 does
  not cover: it left the boot screen, `#blazor-error-ui` is not up, and *nothing reached the console*.
  The last one is the one nothing else in this repo checks, and it is how the gauntlet crash announced
  itself before it blanked the page.
- **Seeded state reaches the DOM.** A hero minted through the API appears on the roster and on its own
  deep-linked page, under the name the server gave it; a funded offer appears in the market at the ask the
  API reports; the ranks page's treasury figure equals the server's.
- **Prices come from the server.** The fixture runs a deliberately non-default breeding fee (1,337), so a
  page that agreed by hardcoding the shipped default fails.
- **Empty states say the true thing.** A roster whose read 503s must not say "no heroes yet"; a market
  whose read 503s must not say "no resting offers". Both are asserted against the real published
  component with the failure arriving off the wire.
- **Onboarding is walked by clicking.** Land, type a name, press Play, meet the Terms gate — and the terms
  document has to actually be *in the bundle*, which is a publish-time property no in-process render test
  can see. It was got wrong once and blocked onboarding outright.
- **The nav goes where it says.** Clicked, not typed, so an href that resolves against the wrong base is
  a failure rather than a passing route test.

## Where the walk stops: the wallet

The onboarding path is land → terms → **a wallet is provisioned** → fund it → buy a hero. Everything from
the third step on is out of reach here, and not for want of trying:

- the wallet is **non-custodial and talks to arkd directly from the tab** — no relay, no server between;
- creating one is not local key generation. `GameWallet.ImportAsync` calls `transport.GetServerInfoAsync()`
  and derives the wallet from the Ark server's own parameters, so with no node there is no wallet, no
  address and nothing to fund;
- the claim after it is a real VTXO spend.

The server's in-memory chain does not bridge this. It simulates the chain the **server** sees, and the
`/api/dev` facade pays invoices on the server's behalf — the browser never calls it, it spends its own
coins. There is no configuration of this harness in which a browser buys a hero without an Ark node.

So the suite drives to that wall and asserts the app is honest at it: a player whose node is unreachable
is **told**, rather than left on a spinner or shown a success they did not get. The funded flow itself is
covered against real arkd by `tests/ArkadeHeroes.Tests.E2E`.

A stub arkd would make a green test out of this and prove nothing about the covenant path it stood in for.

## Why it gates every PR

It needs no infrastructure, and it runs as its own job — in parallel with the unit job, not after it, so a
PR's wall clock is the slower leg rather than the sum.

Measured on the run that introduced it (`30673219593`): **unit job 2m30s, this job 5m38s.** So it is the
slower leg and it roughly doubles PR latency. That is the honest price, and it is worth paying at
5-6 minutes — but it is a price, not a free ride, and it is the number to re-check if this job grows.

That reverses an earlier call, on the strength of what the suite now catches: a page that renders while
throwing, a price that disagrees with the server, an empty state standing in for a failed read. None of
those are visible in a diff. The repo's own history is the rest of the argument — three E2E tests sat red
for weeks behind a manual-dispatch gate.

Only `e2e-test` remains off the PR path, because it genuinely needs the regtest docker stack.

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

The same cache will also keep serving a file you deleted from `wwwroot`. Deleting `docs/TERMS.md` and
`wwwroot/terms.md` and republishing still emitted `terms.md` into the bundle — worth knowing before you
conclude a test is vacuous when it is your experiment that did not bite.

## The two allowances in the console check

"Zero console errors" is asserted with exactly two exclusions, both listed in `PageSession`:

1. **The unreachable Ark node.** The fixture refuses `localhost:7070` and `localhost:3000` deliberately —
   a suite whose result depends on whether a regtest stack happens to be running is not a gate — and that
   one condition produces a refused request plus a retrying batch-stream log. Excluded by the origin a
   message came from and by `NArk.Transport.RestClient` appearing in a logged stack, which no game API
   call passes through.
2. **The headstone probe.** `/api/heroes/{id}/tombstone` 404s for a living hero *by design*, so a burned
   hero shows a grave instead of "couldn't load this hero". Listed by exact path, never by status — a
   blanket "ignore 404s" would hide a page requesting a route that no longer exists.

Both are narrow on purpose. A third exemption should cost somebody a line and a reason.

## What it still does NOT cover

- **No money paths in the browser.** See "Where the walk stops" above.
- **No visual or layout assertions.** No screenshots, no pixel diffs. An ugly page passes.
- **One browser.** Chromium only. No Firefox or WebKit, and no mobile viewports.
- **No two-player flows.** Everything here is one tab; a duel needs two wallets, which needs a node.
- **Not the production host.** The server is real, but it is not the container: no entrypoint script, no
  image, no compression negotiation.

`PublishTrimmed` is still `false`, kept off until a trimmed bundle has been taken "through a funded flow…
with a browser pass over the money paths". This suite is now the browser pass for everything *except* the
money paths, which is the half that still blocks that decision.
