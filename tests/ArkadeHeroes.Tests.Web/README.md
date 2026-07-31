# ArkadeHeroes.Tests.Web — component render tests (bUnit)

Renders the real Blazor pages from `src/ArkadeHeroes.Web` in-process and asserts on the markup they
produce. No browser, no server, no Docker.

This project exists because until it did, **nothing in the repo rendered a page**. The 862-test unit
suite exercises the server, the chain, the genetics and the SDK; the WASM frontend was outside its
reference closure, so page tests were reduced to reading the `.razor` sources back as text and checking
*where* an action lived rather than what a component actually drew. Every UI defect below reached
production through that gap.

## Run it

```sh
dotnet test tests/ArkadeHeroes.Tests.Web -c Release
```

Runs in a few seconds. It is wired into the PR gate in `.github/workflows/ci.yml` next to the unit
suite, because it costs about the same.

## What it covers

Four real regressions, each with a test that fails when the bug is put back:

| Defect | Pinned by |
| --- | --- |
| A Send form rendered at zero balance — a form whose only possible outcome was an error | `WalletSendFormTests` |
| An empty roster that said "you have no heroes" when the read had merely **failed** | `RosterEmptyStateTests` |
| A match in `accepted` state vanishing from the **defender's** view — it fell through every bucket | `MatchVisibilityTests` |
| `/play` labelling the Gauntlet "free" when it charges an entry fee | `StakeLabelTests` |

Plus `SmokeTests`: every page under test renders signed out and signed in without throwing out of a
lifecycle method, and opening the roster is proven to issue no `POST` — a page that bills you for
looking at it is the failure that costs real sats.

The bucket tests are written as a partition rather than as one case: *no match involving one of my
heroes may fall through every bucket, in any status*. The original bug was a missing predicate, so the
guard has to be about coverage, not about the one status that was missed.

Assertions are grounded in the code they describe wherever possible. The fee labels are checked against
`Gauntlet.Fee` rather than a copied number, so if the Gauntlet is ever genuinely made free the test asks
for the "free" chip instead — the label is pinned to the truth, not to a string someone once typed.

## How it is wired

- The **real** `ArkadeHeroesClient` runs over a fake `HttpMessageHandler` (`FakeApi`), so responses go
  through the SDK's own serialization. A canned payload that no longer deserializes into the DTO a page
  reads fails here exactly as it would in a browser.
- An unstubbed route returns **404**, never an empty success. A page silently swallowing a call it did
  not expect is the failure mode this project exists to expose.
- `GameWallet` is substituted at the **facade**. Its read methods bottom out in NArk types — a server
  info record, a derived contract — that cannot be constructed without a live Ark server, so those
  methods are `virtual` purely to give this project a seam. That keyword is the only production change
  the harness needed.

## What it deliberately does NOT cover

This is a renderer, not a browser. It cannot see:

- **WASM startup** — the runtime booting, the boot manifest resolving, `Program.cs` running. A static
  initializer that throws before the first component exists is invisible here by construction, which is
  how the content-pack `TypeInitializationException` reached players.
- **Trimming / IL linking.** The published bundle is a different artifact and nothing here builds one.
- **JS interop for real.** `IJSRuntime` is bUnit's loose stub, so a call is recorded, not executed. A
  broken `.js` file, a missing `_content/` asset or a real clipboard/IndexedDB failure passes.
- **CSS and layout.** These tests read markup. An element that renders but is invisible, off-screen or
  behind a modal passes.
- **Real wallet and covenant flows.** No keys, no signing, no arkd, no chain. Those live in
  `tests/ArkadeHeroes.Tests.E2E` against the regtest stack.
- **Anything two players must see at once.** Each test renders one component in one context.

The first three are covered by `tests/ArkadeHeroes.Tests.Browser`, which drives the published bundle in
a real Chromium. The last two are `tests/ArkadeHeroes.Tests.E2E`.
