# Recovery — reclaiming your heroes and funds without trusting anyone

Arkade Heroes is non-custodial: the server never holds your keys, your heroes are
real Arkade assets in *your* wallet, and every value-bearing action is either
covenant-enforced or reclaimable by you alone. This document is the map of how you
get everything back when something goes wrong — from "I lost my laptop" to "the game
server is gone" to "the Arkade operator itself disappeared."

There are three independent layers, each stronger (and rarer to need) than the last.

## Layer 1 — you lost your machine: restore from your 12 words (SHIPPED)

Your wallet is a standard BIP39 seed. The mnemonic (printed by `backup`, guarded by
you) is the ONLY thing needed to recreate the exact same wallet — same address, same
on-chain heroes, same funds.

```
restore <your twelve words>      # alias: import
```

- The client refuses to overwrite an existing wallet (restore into a fresh
  `ARKADE_HEROES_HOME`), validates the phrase up front (BIP39 word list + checksum,
  via `SelfCustodyWallet.ValidateMnemonic`), and honours
  `ARKADE_HEROES_WALLET_PASSPHRASE` for an encrypted seed.
- The same words re-derive the same Arkade address, so the assets sitting at that
  address — your heroes and items — are yours again. `register <name>` re-joins a
  server with the recovered wallet.
- **The server is not involved.** Progression (levels) is portable too: your signed
  receipts replay your level chain (`verify-receipts` / `ReceiptVerifier.ReplayLevel`)
  independently of any server.

Proven live: `WalletRestoreE2ETests.Wallet_RestoresFromMnemonic_SameAddress_RecoversFunds`
(same address re-derived, funds recovered) and the `WalletLogin*` suites (the wallet
alone resumes your player via a signed challenge — no password, no custodian).

## Layer 2 — a match/offer stalled: reclaim your covenant stake (SHIPPED)

Value you've committed to a covenant — a wager stake, or an item/hero you listed for
sale — is never stuck. Each covenant carries a **timelocked refund/reclaim leaf** that
pays only you, enforced by the script, needing no counterparty and no server.

```
refund <matchId>        # reclaim your wager stake after its refund window
canceloffer <offerId>   # reclaim an unsold item/hero listing after its window
```

- The refund/reclaim transaction is rebuilt **locally** from the public escrow/offer
  parameters (`GET /api/matches/{id}/escrow`, `GET /api/offers/{id}/params`) — the
  address commits to those params, so you can reconstruct and spend it yourself even
  if the server lies or is down. The client gates on the timelock (esplora MTP) and
  submits exactly once (see the timelock invariants in `contracts/README.md`).
- A refunded match now also drops out of the open/accepted lists automatically
  (per-party funded probe + abandonment window).

Proven live: `ClientRefundFlowTests` (trustless-rebuild address equality asserted, the
stake reclaimed with no server) and `CovenantOfferProbe`'s reclaim leg.

## Layer 3 — the Arkade operator vanished: unilateral exit (DESIGN — see status)

Layers 1–2 assume the Arkade server (arkd) is still alive to co-sign offchain spends.
The final backstop is for the scenario where the **operator itself** disappears or
turns malicious: Arkade VTXOs can always be redeemed **on-chain** without cooperation,
via a *unilateral exit* — you broadcast the pre-committed VTXO tree branch to Bitcoin
L1 and, after the exit timelock matures, claim your coins to an address you control.

This capability is **already implemented in the NArk SDK** — Arkade Heroes does not
need to build the exit protocol, only to wire and surface it:

- `UnilateralExitService`
  - `StartExitForWalletAsync(walletId, claimAddress, ct)` — start a stateful exit of
    every VTXO in the wallet, then drive it with `ProgressExitsAsync` /
    `GetActiveSessionsAsync`.
  - `BroadcastExitChainAsync(...)` → `ClaimMaturedExitAsync(...)` — the one-shot,
    stateless equivalent (no exit-session storage needed).
- `ExitWatchtowerService.CheckAndRespondAsync(...)` — monitors L1 for a partial tree
  broadcast (e.g. the operator trying to sweep your VTXO early) and auto-starts your
  own exit in response; `ExitWatchtowerBackgroundService` runs it autonomously. This
  is the "watchtower handoff": you (or a service you delegate to) keep watch so a
  malicious sweep can't outrun your exit.

### Wiring it into the game wallet (the scoped next step)

`SelfCustodyWallet` today registers `AddArkCoreServices()` + the Arkade transport, but
**not** the exit services — because unilateral exit fundamentally needs **Bitcoin L1
access** (to broadcast the exit chain and watch for maturity), which the game wallet
does not currently connect to (it speaks only to arkd). To surface Layer 3:

1. Register an L1 blockchain provider — `IBitcoinBlockchain` — pointed at the regtest
   Bitcoin RPC / esplora / NBXplorer the stack already runs (the refund flow already
   reads esplora MTP, so the endpoint is known).
2. `services.AddInMemoryExitStorage()` (or an EF-Core exit-session storage) +
   `services.AddUnilateralExit()`; optionally `AddExitWatchtowerBackgroundService()`
   for autonomous watch and `AddVirtualTxAutoFetch()` to pre-store exit data.
3. Add a thin `SelfCustodyWallet.StartUnilateralExitAsync(claimAddress)` that resolves
   `UnilateralExitService` and calls `StartExitForWalletAsync`, and a client `recover`
   command that reports the exit sessions and their maturity.

### Status and why it is not yet shipped as code

Deliberately **not** shipped as game code yet, and documented as design instead:

- It cannot be verified in the current E2E harness. A meaningful test needs a real
  "operator gone / malicious sweep" environment — stop arkd, broadcast a tree branch
  on L1, mine past the exit timelock, then claim — none of which the regtest E2E
  presently exercises. Shipping **unverifiable, safety-critical recovery code** would
  be worse than a clear design: a player reaching for a broken `recover` in an
  emergency is the worst possible failure.
- It needs the L1-provider wiring above, which is a real integration (not a one-liner)
  and belongs with a live unilateral-exit test that proves the whole
  broadcast → mature → claim path end-to-end.

When that test environment exists, wire the three steps above and this section moves
from DESIGN to SHIPPED. Until then, Layers 1–2 cover every routine recovery, and
Layer 3's protocol guarantee already exists in the VTXO structure — the exit is always
*possible*; this is only about making it a one-command convenience in the game client.

## Summary

| Scenario | Backstop | Status |
|---|---|---|
| Lost machine / new device | Restore from 12 words (heroes + funds + level chain) | ✅ shipped, live-tested |
| Match or listing stalled | Timelocked covenant refund / reclaim (local rebuild, no server) | ✅ shipped, live-tested |
| Game server down (arkd alive) | Same as above — refund/reclaim need no server | ✅ shipped |
| Arkade operator gone / malicious | Unilateral exit + watchtower (NArk `UnilateralExitService`) | 🔧 design — needs L1-provider wiring + a live exit test |

Your heroes are yours because they are on-chain assets in your wallet, and your stakes
are yours because the covenant says so — recovery is the proof of both.
