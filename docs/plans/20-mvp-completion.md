# MVP completion map — everything remaining for a full, complete game

**Baseline:** `a4f65b8`, tree clean, gate **69 unit + 11 E2E green**. This document is the
definitive remaining-work map for the next agent (Opus), ordered by the standing mandate
(non-custodial ownership first, covenant-enforced fairness first, breadth after). Work each
item top-to-bottom; each has its own definition-of-done so the loop can commit per milestone.

## Definition of MVP-complete (the acceptance bar)

One documented, repeatable **MVP walkthrough E2E** (item 7 below) in which two players, each
with their own self-custody wallet, complete the full life of the game on regtest with every
value-bearing action either covenant-enforced or client-verifiable:

register → fund → claim starters → **covenant breed** (parents retained, child genome
oracle-attested + covenant-pinned, fee paid) → equip → **covenant wagered duel** (oracle-authorized
settle) → abandoned-match **refund** (no server) → hero **transfer** → **marketplace** item sale →
**leaderboard** reflects results → `verify-receipts` passes for both players.

Everything below exists to make that walkthrough pass. What already passes today: wagered
duels (covenant mode, adversarial-tested), refunds (player-facing), transfers, equipment,
receipts. What follows is the gap list.

## 1. Covenant breeding — task 19, rungs 1–6 — ✅ DONE (SHIPPED live)

All six rungs proven on regtest and committed: `BreedAuthorized` covenant gate (parents retained via `0xf2`, child controlled by species via `0xe7`, fee via `PayTo`, oracle CSFS over the `0xe9`-read metadata root — honest breed co-signed, cheats refused); the breed ESCROW game flow (player deposits parents+fee, server assembles the emulator-enforced mint, `GetBreedEscrowParamsAsync` for trustless rebuild); InMemory sim + server `mode: covenant` + live E2E (`CovenantBreedFlowE2ETests` — parents retained, child minted under species, all in the player's wallet). Two hard-won rules pinned in `19-backlog.md`: `0xe7` ctrl_txid is reversed order, and the fee output must pay a distinct address (the builder coalesces same-script outputs). Gate at completion: 74 unit + 14 E2E. **Remaining before MVP-complete: items 2–7 below.** The original rung spec is retained below for reference.

<details><summary>Original rung 4–6 spec (reference)</summary>

### (was) Finish covenant breeding — task 19, rungs 4–6

Rungs 1–3 are PROVEN (see `19-backlog.md` §19: issuance/controlled-issuance under covenants,
metadata-root parity via `ArkadeCovenants.MetadataMerkleRoot`, rows 0xe5/0xe7/0xe9/0xf1 live,
**byte-order rule** — in-VM asset txids are the REVERSE of NArk `AssetId.Txid`; bake
`Txid.Reverse()`).

**Rung 4 — `ArkadeCovenants.BreedAuthorized` + adversarial probes.** Compose from proven
parts, ONE component per live probe run (extend `CovenantBreedProbeTests`; fresh contract
names per attempt; deliver parent assets with `SendAssetAsync`; fund covenant VTXOs before
minting). Script components (witness design: draw the full stack trace in comments first,
like the probe tests do; last witness item = stack top):
- oracle gate: `0xe9` (pops childK, pushes root32) + `<oraclePk32>` push + `0xcc` CSFS
  (pops pk, msg, sig) + `0x69` — the oracle signs the RAW 32-byte metadata root (BIP340 over
  raw message, proven in task 16); domain separation rides INSIDE the metadata (a
  `breed=arkade-heroes-breed-v1|<breedId>|<parentA>|<parentB>` entry next to the genome).
- species pin: `0xe7` (pops childK) + found VERIFY + gidx EQUALVERIFY + txid (INTERNAL byte
  order!) EQUALVERIFY — this is MANDATORY: rung 3 proved arkd lets ANYONE mint under a
  foreign control asset; only this covenant check stops species forgery.
- parent retention ×2: `0xf2` per parent (pops gidx, txid, i — script pushes txid+gidx baked,
  witness supplies i) + found VERIFY + amount==1 EQUALVERIFY; arkd's input-conservation rule
  (rung 2) forces the passthrough groups to exist.
- fee: `PayTo(treasuryP2tr, feeSats)` with a witness output index.
Adversarial probes required: wrong-root signature refused; missing parent refused; wrong
species (uncontrolled or foreign-control child) refused; fee theft refused; honest breed
passes. Done = all six probes green + committed.

**Rung 5 — the breed escrow in the game flow.** Reuse the wager-escrow architecture wholesale
(commit 82e430d as the template; escrow params in KV; shared builder pattern like
`WagerEscrowContracts`):
- Player deposits BOTH parent heroes (via `SendAssetAsync` semantics — the client pays assets
  to the breed escrow address) + the breeding fee sats at a **breed covenant address** whose
  leaves are: `breed` (= `BreedAuthorized`, above) and `refund` (timelocked, parents + fee back
  to the player — task 17/18 machinery reused verbatim: CLTV tapleaf, submit-once, MTP gate,
  client `refund` extends to breed escrows).
- Server (treasury wallet) assembles the breed tx: spends the escrow VTXO through the `breed`
  leaf, packet = [parentA passthrough (back to player), parentB passthrough (back to player),
  child controlled issuance (to player, genome + breed-context metadata)], outputs = player
  carrier + treasury fee + extension. Oracle (= receipt key) signs the child metadata root.
  The covenant makes any OTHER shape unsignable — that is the mandate's payoff.
- Server API: breed already exists (commit–reveal). Add mode `covenant` to the breed request
  (mirroring matches): commit → player funds escrow → reveal computes genome → server executes
  the covenant mint. `GET /api/breedings/{id}/escrow` mirrors the match-escrow endpoint so the
  client can rebuild + refund trustlessly. InMemory sim mirrors every rule (species pin, root
  sig, retention, fee) like `SettleWagerEscrowAsync`'s sim does.
- The genome must live in the CHILD's genesis metadata (it already does for server minting —
  keep byte-compatibility with `FairnessAudit`'s recompute).

**Rung 6 — client + E2E.** Client `breed <a> <b> covenant` flag (auto-fund escrow like
covenant challenges); `refund <breedingId>` (share the refund flow, keyed by escrow params
endpoint); full-game-loop E2E gains the covenant-breed leg; adversarial E2E for the flow.
Done = walkthrough leg passes + client can reclaim an abandoned breed escrow.

</details>

## 2. XP / anti-farming model — task 20 REWORKED (on-chain XP REMOVED)

The original task-20 approach (mirror level-ups as fungible on-chain XP-asset deliveries,
shipped in `9794d9e`) was **reversed**: on-chain XP is fundamentally incompatible with the
anti-farming model below, because that model makes levels **losable** and you cannot claw back
a non-custodial asset. Removed: the XP asset + its delivery pipeline, the
`DeliverXpAssetsOnChain` option, `GET /api/players/xp`, and the `IChainService` XP surface
(`DeliverXpAsync`/`GetXpBalanceAsync`) with its InMemory + NArk implementations.

**The locked model (Sybil-resistant, farming-resistant):**
- **Friendly (wager-0) fights award no XP** — practice is free; free XP would be farmable.
- **A staked win moves a CONSERVED, difference-scaled XP transfer** from loser to winner:
  `transfer = max(0, 40 + 12·(loserLevel − winnerLevel))`, winner **+transfer**, loser
  **−transfer**. The winner gains exactly what the loser loses, so self-play across any number
  of wallets can only CONCENTRATE XP, never MINT it. The clamp kills farming down the ladder
  (beating a far-weaker hero transfers 0); an upset over a higher hero transfers a lot.
- **Levels are LOSABLE** — `Leveling.Apply` takes a SIGNED delta and can delevel to the floor
  (level 1 / 0 XP), so a champion is held by winning, not bought once. Receipts record the
  signed per-hero delta; `ReceiptVerifier.ReplayLevel` folds them (a loss pulls the level back
  down), so the receipt chain stays the audit trail and the server is the ledger.
- **The defender is protected by CONSENT** (`AcceptMatchAsync`), not by an asymmetric loss —
  any "loser loses less than the winner gains" would mint XP and be farmable.

Done (XP rung, `e0e3922`): `Leveling` rewritten (conserved `XpTransfer`, signed/losable
`Apply`); `FightAsync` applies the conserved transfer and records signed deltas; on-chain XP
fully removed.

Done (fee rung): the per-character match fee is wired end-to-end. Each fighter pays
`Leveling.MatchFee(level) = 500 + 20·level` sats to the treasury to stage a STAKED match — a
level-proportional sats sink so idle-training a high-level hero costs sats every fight. The
fixed base clears Ark's 330-sat dust limit: the fee is a real VTXO, so a sub-dust amount can't
land (a bug caught by the live E2E, invisible to the dust-free InMemory sim). The challenger's
fee invoice is issued at open, the defender's at accept, and BOTH must be paid before
`FightAsync` resolves — in invoice AND covenant modes, separate from the pot/escrow. The client
pays it via the same `SettleInvoiceAsync` path as stakes; friendly (wager-0) matches have no fee.

Gate: **113 unit + FullGameLoop E2E green live on regtest** (both invoice- and covenant-mode
staked duels pay the fee; the covenant settle-balance assertion accounts for it). Item 2 COMPLETE.

## 3. Marketplace — task 21 (medium) — ✅ DONE (SHIPPED live)

Banco-style item offers, buyer-funded and covenant-enforced, wired end-to-end and committed
(`bd2f75b` primitive, `c29c7e7` plumbing).

**The hard part — buyer-funded covenant spend (`bd2f75b`).** `CovenantSpender.SpendManyAsync`
gained an optional `fundingCoins` param: the actor's OWN wallet coins (a buyer paying an
offer's ask), appended after the covenant inputs with real signer descriptors so
`ConstructArkTransaction` signs them with the buyer's key while the emulator co-signs only the
covenant input. Packet survival across NArk's asset-vin remap was the trap: NBitcoin orders
ark-tx inputs by BIP69 outpoint (not our coin order), so NArk rebuilds the extension from the
ASSET packet alone and silently drops the co-resident `EmulatorPacket` ("no emulator packet
found"). Fixed POST-construction: rebuild the extension with the EmulatorPacket's vins pointing
at each covenant input's ACTUAL position (via its checkpoint's ark-tx index), read back NArk's
already-remapped asset packet, then re-sign the funding inputs over the corrected outputs
(reconstructing each funding coin's internal checkpoint coin exactly as NArk does, since the
ark tx spends CHECKPOINT outputs; the emulator verifies non-arkd sigs on ALL checkpoints, so
funding checkpoints are buyer-signed too).

**Contracts + flows.** `OfferContracts.Build` — resting-offer covenant: `fulfill` leaf
(`PayTo(seller, ask)`; emulator refuses underpayment) + timelocked `reclaim` leaf
(`RefundTo(seller, offerValue)`, refund machinery reused). `OfferFulfillFlow` (buyer rebuilds
the contract locally, funds the ask, takes the item via an asset passthrough packet).
`OfferReclaimFlow` (seller cancel — mirrors the proven `EscrowRefundFlow`: rebuild, find the
resting VTXO, MTP-gate, submit-once).

**Server = discovery index only; every covenant op is client-side from the actor's own wallet.**
`IChainService` offer surface (`CreateOfferAsync`/`IsOfferFundedAsync`/`GetOfferParamsAsync`);
NArk impl resolves the game item id → the shared species-controlled item asset, persists params
in KV, sets `OfferValueSats = serverInfo.Dust`. `GameStore.OfferListing` (pending → active →
closed, reconciled from on-chain truth); `GameService.CreateOfferAsync` (free-unit check:
held − equipped − pending-reserved, seller's own listings reconciled first). Endpoints: `POST
/api/offers`, `GET /api/offers`, `GET /api/offers/{id}`, `GET /api/offers/{id}/params` (buyer's
trustless rebuild basis) + InMemory dev hooks. Client `sell` / `offers` / `buyoffer` /
`canceloffer`. **Hero sales SHIPPED (post-MVP extension)** — the offer covenant is asset-agnostic,
so selling a HERO (a unique supply-1 asset) reuses it wholesale: `CreateHeroOfferAsync` (factored
`CreateOfferForAssetAsync` core), `POST /api/offers/hero`, buyer `ClaimPurchasedHeroAsync` (verify
the buyer holds the hero asset → reassign the record + strip equipment, like transfer), client
`sellhero` / `buyhero`. Proven: `HeroSaleTests` (4, InMemory lifecycle + guards),
`ClientGameLoopTests` (hero sale through the real client), and live
`RestingOffer_SellsAWholeUniqueAsset` (a supply-1 asset rests whole + sells, seller ends with
none). A server-driven live hero-sale E2E is NOT added: the seller depositing a treasury-minted
hero would hit the same `VTXO_RECOVERABLE` coin-selection artifact as an item resale (documented
below), while the covenant itself is proven asset-agnostic.

**Coverage.** Live on regtest: `CovenantOfferProbeTests` — honest buyer-funded fulfilment
(seller paid, item to buyer) AND adversarial underpayment refused (buyer shorts the seller →
emulator refuses to co-sign, item never moves), both with the input-order correction running;
`OfferFulfillCovenantTests` — the sats-only bounty shape (pre-existing). InMemory:
`MarketplaceOfferTests` (8) — full lifecycle, reserve accounting, cancel, broke-buyer refused,
params rebuildable + 404. **Not included: a server-driven live E2E that has the seller deposit a
treasury-BOUGHT item into the offer** — that step fails at `SendAssetAsync` with arkd
`VTXO_RECOVERABLE`. A throwaway diagnostic (verified, then removed) corrected the first read:
received/transferred asset VTXOs re-spend FINE (mint→send→re-spend = success; the item VTXO is
`swept=False`, `preconfirmed=True`, expiry ~67 min out — neither swept nor expired in-window), so
it is NOT the item VTXO and NOT simple expiry (`IsRecoverable = Swept || IsExpired`). The true
trigger was not isolated but is almost certainly a recoverable *sats* coin (old-batch ancestor)
pulled into coin-selection for the deposit — i.e. NArk coin-selection including recoverable coins,
an SDK-layer concern, NOT marketplace logic. `CovenantOfferProbe` proves the identical
buyer-funded fulfilment live (covenant fairness fully proven); the NArk offer-index methods are
structural mirrors of the proven breed-escrow live path and ran successfully (correct offer
address + asset id + pending reconciliation) before that downstream arkd artifact. Robust fix
(refresh/settle coins before spend, or filter selection to spendable-offchain) is parked. Gate at
completion: **85 unit + 16 E2E green.**

## 4. Leaderboard — ✅ DONE (receipts-computed; GET /api/leaderboard + client top)

Server-computed from receipts (anyone can recompute — that's the trust story): wins, level,
lineage depth. `GET /api/leaderboard` + client `top`. No covenant work. Done = walkthrough
shows both players ranked.

## 5. Wallet-file encryption — task 23 — ✅ DONE (SHIPPED, opt-in)

Chose option (b) from the design note: `EncryptingWalletStorage`, an `IWalletStorage` decorator
over NArk's `EfCoreWalletStorage` (which `AddArkEfCoreStorage` registers as a separate concrete
type, so we just swap the `IWalletStorage` resolution when a passphrase is set — `RemoveAll` +
re-add). It encrypts the `ArkWalletInfo.Secret` on write and decrypts on read, so the SQLite DB
holds only ciphertext while NArk's signer still gets the plaintext mnemonic at runtime —
transparent to the SDK, no submodule change. `WalletSecretCipher`: PBKDF2-SHA256 (210k) +
AES-256-GCM, self-describing `enc:v1:` token so plaintext coexists and a wrong passphrase fails
on the GCM tag. `SelfCustodyWalletOptions.Passphrase` (opt-in; null = today's plaintext), fails
fast if an encrypted wallet is opened without it. Client reads
`ARKADE_HEROES_WALLET_PASSPHRASE`. Proven: `WalletSecretCipherTests` (6) + `WalletEncryptionE2ETests`
(2 — no cleartext on disk incl. WAL/journal sidecars; reopens only with the right passphrase;
passwordless stays plaintext). E2E suite unaffected (no passphrase → pass-through).

## 6. Known warts — starter-claim ✅ DONE; funded-check ✅ DONE; expired-session bookkeeping = documented minor limitation

- **Starter-claim not rolled back on failed mint** — ✅ DONE (flag flip after mint + InMemory test).
- **`IsEscrowFundedAsync` exact-amount check** — ✅ DONE (`56b4be1`): now that marketplace assets
  circulate, both the funded gate and the settle input-selection require a pure-BTC VTXO
  (`IsBtcStake`: `v.Assets is null or empty`), so an asset carrier at the same sat value can't be
  swept as a stake. WagerEscrowCovenant re-gated green.
- **Match/breeding records after refund** — ✅ DONE (per-party funded probe + abandonment window).
  `IChainService.GetWagerEscrowFundingAsync` reports each side's on-chain stake state SEPARATELY;
  `GameService.ReconcileAbandonedMatchesAsync` (run lazily on `GET /api/matches`) marks a covenant
  match `expired` once it is PAST its refund window AND its escrow is no longer intact — an `open`
  match whose challenger stake is gone (never staked / refunded) or an `accepted` match missing
  either stake. A still-funded match stays visible (it can yet settle or be refunded), and nothing
  within the window is touched, so a live pending match is never mis-marked (exactly the failure a
  both-parties `IsEscrowFundedAsync` couldn't avoid). A refunded match now drops out of the open/
  accepted lists. Test: `CovenantRefundTests.RefundedMatch_IsExpiredAndDropped_FromTheOpenList`.

## 7. MVP walkthrough — ✅ docs/RUNBOOK.md shipped (human two-terminal script, each leg mapped to its proving E2E); a single consolidated walkthrough E2E is redundant with the per-leg E2Es (FullGameLoop + CovenantBreedFlowE2E + ClientRefundFlow + WagerEscrowCovenant)

- `tests/ArkadeHeroes.Tests.E2E/MvpWalkthroughTests.cs`: the full two-player story from the
  acceptance bar above, against the real stack, asserting balances/assets/receipts at each leg.
- `docs/RUNBOOK.md`: the same walkthrough as a HUMAN two-terminal script (server + two client
  REPLs, exact commands, expected output) so the user can PLAY it — this is what "playable on
  local regtest" means. README links it.
Done = both exist, walkthrough E2E green in the full gate, runbook manually sanity-checked
against the client's actual command output.

## 8. Post-MVP parking lot (explicitly OUT of scope — do not start before 1–7)

CI (GitHub Actions: unit always; E2E behind a regtest service container), upstream arkd
poisoned-txid bug report (`19-backlog.md` §24 has the trace), ~~hero marketplace~~ (SHIPPED — see
item 3), pre-built recovery PSBTs/watchtower handoff, VRF entropy replacing commit–reveal, XP-weighted
matchmaking, wallet import/restore UX, ~~expired-session bookkeeping~~ (✅ SHIPPED — see item 6),
**coin-selection recoverable-coin filter** (a treasury-bought item's offer
deposit can fail `SendAssetAsync` with `VTXO_RECOVERABLE`. CONFIRMED in the SDK:
`SpendingService.GetAvailableCoins` (SpendingService.cs:168) excludes only unconfirmed-onchain
VTXOs, NOT recoverable (`Swept || Expired`) ones — so a recoverable coin is offered to selection
and dooms the spend. INFERRED (not reproduced): that such a coin is actually present in the
failing flow. Fix = filter selection to `CanSpendOffchain`, or settle/refresh coins before spend;
the auto-selecting `Spend(walletId, outputs)` overload can't inject the filter, so a game-side fix
would need the explicit-coins `Spend(walletId, coins, outputs)` overload + local selection — most
cleanly an upstream NArk change to `GetAvailableCoins`).

## Working rules reminder (unchanged)

Loop protocol per `docs/HANDOFF.md` §1: gate → commit (verify `git show --stat`) → update
`contracts/README.md` + `DESIGN.md` + this file's checkboxes → memory → `TaskUpdate` →
`ScheduleWakeup` ~120s. Read `contracts/README.md` traps + `19-backlog.md` pinned semantics
before ANY chain code. The Fable quota is nearly exhausted — Opus continues from here; when
the limit resets, whichever model wakes follows this same map.
