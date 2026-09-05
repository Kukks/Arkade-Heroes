# Arkade Heroes — design

A breeding + battling game (CryptoKitties-lineage genetics, plus levels, skills, equipment, and PvP) where heroes and payments live on [Arkade](https://docs.arkadeos.com). This document covers the game rules and the chain integration, including the staged path from a server-authoritative v1 to covenant-enforced gameplay.

Grounded in: `docs/research/arkade-kitties.md` (on-chain asset + breeding contract design), `docs/research/covenant-game-patterns.md` (coinflip/emulator/banco patterns), `docs/research/dotnet-sdk.md` (NArk APIs and regtest harness).

## 1. Game model (ArkadeHeroes.Core — pure, deterministic)

### Genome

32 bytes, immutable from birth, following the ArkadeKitties "commit the genome, derive everything" principle:

| Bytes | Trait |
|---|---|
| 0–4 | Stat genes: STR, VIT, AGI, INT, LUK |
| 5 | Element (mod 8: Ember→Gale→Terra→Tide→Volt→Frost→Radiant→Umbral ring) |
| 6–7 | Skill genes A, B (index the 16-entry skill catalog) |
| 8–12 | **Growth genes** (per-stat gain per level — hidden potential) |
| 13 | Cooldown gene (breeding recovery, like kitties byte 15) |
| 14–15 | Appearance (name palette + title; console art stand-in) |
| 16–31 | **Trait map**: 8 dominant/recessive categories (6 cosmetic + ElementAffinity, Temperament) — see `Core/Genetics/Traits.cs`. Zeroed on gen-0 and on recruits, so rarity can only enter the world by breeding mutation |

Growth genes are the breeding meta: invisible at level 1, dominant at high level, so lineages matter more than stat screens.

### Breeding

`GeneMixer.Mix(parentA, parentB, entropy)` mirrors the ArkadeKitties design doc's `mixGenomes`: per-trait crossover selected by entropy bytes (even → A, odd → B), mutation when the selector byte ≥ 248 (1/32 per trait) rerolled from `SHA256(A ‖ B ‖ entropy)`. Generation = `max(parents) + 1`. Cooldown doubles per breed (cap 2⁷) scaled by the cooldown gene.

**Entropy is commit–reveal**: server commits `SHA256(serverSeed)` when the breed request opens; the player supplies a nonce; entropy = `SHA256(serverSeed ‖ parentAId ‖ parentBId ‖ nonce)`. Seed + nonce are stored with the child (and in its asset metadata), so anyone can re-derive the genome and audit the server. This is the same trust posture as coinflip's secrets and maps 1:1 onto the future covenant version.

### Progression

- XP: a **conserved transfer** on staked wins only — the winner gains, and the loser loses, `max(0, 40 + 12·(loserLevel − winnerLevel))` (so it zeroes past a 4-level gap — no farming down the ladder — and the loser can delevel), **clamped to what the loser actually owns** (`Leveling.PayableTransfer`; without the clamp, beating a hero at the level-1/0-XP floor MINTS the difference). Friendly (unstaked) matches award no XP and don't count toward the ranked leaderboard. Curve `80 + 45·level^1.35`, cap 50.
- The **only XP mint** is the PvE gauntlet (`Gauntlet.XpForRun`), and it pays nothing at or past `xpLevelCap` (10). Trials award a title/score, not XP; duels and squad matches only move XP between heroes. So a hero that has never run the gauntlet has nothing to win or lose in a staked fight — measured at ~84% of staked duels in a fresh arena. See `tools/ArkadeHeroes.Sim`.
- Skills: `Strike` plus the **gene-A skill from level 1** (so no hero is ever Strike-only); gene-B at level 6; `Elemental Burst` at level 9. The three levels are `CombatConfig.GeneSkillALevel/GeneSkillBLevel/BurstLevel`, not constants.
- Equipment: 3 slots (weapon/armor/trinket), fixed catalog, flat stat mods, priced in sats. Each item type is a **fungible Arkade asset** (issued lazily by the treasury on first sale, supply 1000, species-controlled, item id in genesis metadata). Buying pays the price and delivers one unit to the player's wallet; equipping allocates a held unit (one unit backs at most one equipped hero); unequip frees it. Loadouts don't travel on hero transfer — item assets stay with the seller's wallet, so the server strips equipment when a hero changes owners. Banco-style item trading between players is live (resting covenant offers — see the covenant roadmap below).

### Combat

Deterministic auto-battler (`BattleEngine.Fight(a, b, matchSeed)`): initiative by speed, skill choice by `CombatConfig.SelectionPolicy` — **`Tactical` by default**, which opens with a buff, debuffs a durable target and drains when hurt, so status skills are worth casting (`Greedy` is the original always-max-damage behaviour) — damage = `power · scale / (defense + 25)` with element ring multipliers (1.3×/0.75×), crit (luck), dodge (speed), ±10% variance — all rolls from a seeded xoshiro256** stream. Max 60 turns, then HP-fraction decision. Output is a replayable `BattleResult` event log; the match seed is commit–reveal over both players' nonces, so either player can re-run the engine and verify the outcome.

## 2. Chain integration (ArkadeHeroes.Chain) — covenant-first, non-custodial

**Standing mandate (owner directive):** players own their characters and progression **non-custodially** — the game server must never hold player keys — and **covenants enforce fairness across the board**. No architectural shortcut that violates either is acceptable as an end-state; anything interim must be additive toward this target and is tracked for removal.

- **Keys**: player wallets live in the client (mnemonic generated and stored locally, never transmitted). The server knows players only as Arkade addresses/pubkeys.
- **Assets**: heroes and item units sit in player wallets from mint onward; the server's treasury signs only its own outputs (mints, payouts).
- **Payments**: fees and stakes are paid *by the client's wallet* to per-session treasury invoice addresses; the server verifies receipt on-chain — it cannot spend player funds because it never can.
- **Progression**: derived from commit–reveal-verifiable events whose proofs the player holds (receipts now, XP-asset deliveries next) — portable across servers, recomputable by anyone.
- **Fairness**: every rule is a covenant leaf; enforcement migrates from server-refusal to emulator co-signing per the roadmap below, and the deterministic derivations (genome mixing, battle replay) are the same bytes in both regimes.
- **Deprecated**: the custodial player-wallet mode (server-held HD wallets) is scheduled for removal once the self-custody client lands; it must not grow new features.

**The covenant leaves are the authoritative rules of the game**, and the running server is an *executor* of the shapes they pin, not the definition of them. Migrating a mechanic to covenant enforcement swaps who refuses an invalid transaction (the emulator's tweaked-key signature instead of server policy) — the transactions, asset structures, and derivations do not change. Note that "the covenant leaves" is **not** the same set as `contracts/*.ark`: three live escrow families (merge, the death-match joint escrow, the marketplace fee leg) were authored straight to bytecode with no `.ark` source, and where a source exists the runtime has drifted from it. `contracts/README.md` enumerates both gaps; read the C# in `Chain/Covenants/` for what is actually enforced.

| Mechanic | Covenant (contracts/) | Transaction shape (live today) | Enforcement today | Remaining gap |
|---|---|---|---|---|
| Hero identity | `arkade_heroes.ark` | asset amount 1, species-controlled, genome+provenance in genesis metadata | on-chain (structure) + server issuance | the `.ark` source is not the runtime artifact — see `contracts/README.md` |
| Breeding | `arkade_heroes.breed` | parents retained (Δ0) + control retained (Δ0) + child fresh-minted (Δ1) with derived genome | **covenant** when `mode == "covenant"` — the `breed` leaf is `ArkadeCovenants.BreedRetainAuthorized`, emulator co-signed; commit–reveal still audited client-side | `BreedCommitRequest.Mode` still DEFAULTS to `"invoice"` (`Shared/Dtos.cs:192`), so an API caller that doesn't ask gets the legacy treasury-mint path. The web client always asks |
| Transfer | `arkade_heroes.transfer` | Δ0 asset move to recipient's VTXO | client-signed from the owner's OWN wallet; the server only verifies the chain shows the recipient holding it (`VerifyHeroOwnershipAsync`) | none — an owner spending their own asset needs no covenant |
| Wagered match | `wager_escrow.ark` | per-party stake VTXOs → oracle-authorized sweep of both to the winner; timelocked per-party refund | **covenant, and now the ONLY path** — `Chain/Covenants/WagerEscrowContracts.cs`, emulator-settled. The server picks the mode (`NonCustodialSettlement`), not the caller: a wagered match refuses `"invoice"` outright | the live taptree diverges from the source: no `forfeitTo*` leaf, and its `refund` requires no party signature |
| Marketplace sales (items + heroes) | `item_offer.ark` | resting offer VTXO; any buyer pays the ask from their own wallet in the same tx | **covenant** — `fulfill`/`reclaim` leaves (`OfferContracts`); the treasury's cut is a second payout the same leaf pins, not a step anyone has to be trusted to take | live `fulfill` drops the source's `itemGroup.delta == 0` and residual re-lock. Hero *bids* (buyer-initiated, unlisted heroes) still route through a treasury invoice — with tournament buy-ins, the last custodial holdings of player money in the game |
| Item shop (catalog) | — none written | fee invoice → treasury issues and sends one fungible unit | server delivers after fee payment | no covenant at all; the resting-offer shape above is the obvious one to reuse |

**Invariants that keep every mode covenant-compatible** (enforced by tests):
1. All randomness is commit–reveal (`SHA256(serverSeed)` published before player nonces) and all derivations are pure functions — `GeneMixer.Mix` is byte-compatible with the design-doc `mixGenomes`, `BattleEngine.Fight` replays from the match seed.
2. Genome, generation, lineage, and the commit–reveal proof are sealed in asset **genesis metadata** — the covenant's `metadataHash` checks bind to data that already exists on-chain today.
3. Heroes/items are **species-controlled asset groups** with the exact delta discipline the covenants require (retain Δ0 / fresh-mint Δ1).
4. Fees, stakes, and payouts are plain Arkade transactions with fixed destinations — the outputs the covenants pin (`scriptPubKey == SingleSig(...)`, `value >= pot`).

**Covenant plumbing is the SDK's** (2026-09-05): the `ArkScriptHash` tagged-hash tweak, the Emulator Packet and the emulator REST client now come from **`NArk.Arkade`** (`ArkadeTweak`, `EmulatorPacket`, `EmulatorClient`), and the game's own copies were deleted after being proven byte-identical. What stays in `src/ArkadeHeroes.Chain/Covenants/` is the composition on top: the covenant bytecode builders (`ArkadeCovenants`), the per-contract leaves (`ArkadeArtifactContract`), the spend pipeline (`CovenantSpender`) and the per-mechanic escrow/refund flows. The regtest emulator is probed at startup and its signer key is surfaced through `/api/chain/info`, so clients can compute covenant keys themselves.

**What "covenant-enforced" actually means here.** Arkade Script opcodes are **not enforced by Bitcoin consensus**. Every leaf we build is an ordinary two-key multisig — `<tweak(emulatorKey, script)> OP_CHECKSIGVERIFY <operatorKey> OP_CHECKSIG` (`Chain/Covenants/ArkadeArtifactContract.cs`) — and the script itself rides an OP_RETURN Emulator Packet, where the **emulator** executes it and signs only if the predicate holds. The SDK is explicit about this: `ArkadeProgramCompiler` refuses an Arkade opcode in the tapscript segment as "not enforceable on-chain" (they are `OP_SUCCESS` there). So migrating a mechanic to a covenant does not make cheating impossible at the consensus layer. It moves the trusted party from *the game server* to *the emulator and the operator jointly*, and it turns the rule from private server policy into a deterministic script that any player can rebuild byte-for-byte from published parameters and read for themselves. That is a real security gain — a party with no stake in the outcome refuses the cheat, and the check is public — and it is not the same claim as consensus enforcement.

**No covenant in the game has a unilateral exit leaf.** `ArkadeArtifactContract.GetScriptBuilders()` emits only collaborative leaves, every one ending `<operatorKey> OP_CHECKSIG` — compare the SDK's own `ArkPaymentContract`, which pairs a collaborative path with a CSV-delayed unilateral one. The timelocked refunds below are trustless of the *oracle, the counterparty and the game server*, but not of the operator: an operator that stops co-signing strands every escrowed stake, item and hero. This is a **liveness** assumption, not a theft hole — the operator alone cannot move anything either, because each leaf also needs the emulator's signature over a passing script — but it is the largest unstated assumption in the model, and closing it means adding an exit leaf to each contract.

### v1 execution mode — real assets, server-executed shapes

- **Hero = Arkade asset, amount 1**, minted via NArk `AssetManager.IssueAsync` with `Metadata = { genome, generation, parentA, parentB, serverSeed, nonce }` and `ControlAssetId` = the **species control asset** the game server mints once at first boot (the ArkadeKitties species-gate concept).
- **Canonicity**: a hero is legit iff its asset's control is the game's species asset (server is the only holder of the control asset, hence the only possible issuer — verifiable on-chain via `GetAssetDetailsAsync`).
- **Ownership**: the player's Arkade address holds the VTXO carrying the hero asset. Mint delivers the asset to the player; trades are plain asset spends.
- **Payments**: breeding fees / item purchases are Arkade transactions (sats) from player wallet to the game treasury address.
- **Transfers**: heroes move between players as plain asset spends (`POST /api/heroes/{id}/transfer`, client `transfer`).
- **Wagered matches**: open → accept → duel. Each side stakes into its OWN escrow contract and the emulator settles the oracle-authorized branch (`Chain/Covenants/WagerEscrowContracts.cs`); see the trust model below. The legacy `"invoice"` mode — both stakes paid into treasury addresses, the pot paid back out by `PayoutAsync` — is GONE: it meant the operator held player money owed to whoever won, and it was the default for any caller that omitted the field. An unwagered match stakes nothing, so it needs no escrow.
- **Server wallet**: NArk over EF Core (`Chain/NArk/`: `GameArkDbContext`, `AddArkEfCoreStorage`, wallet secrets at rest behind `EncryptingWalletStorage`), funded on regtest via `arkd note`.
- The game DB caches hero state for matchmaking/progression; the chain is authoritative for existence + ownership.

### Covenant activation roadmap (contracts are written; this is the wiring order)

1. ~~**Compiler wiring**~~ **DONE**: all three contracts compile with `arkade-os/compiler`; artifacts in `contracts/build/`.
2. ~~**Emulator Packet builder**~~ **DONE**: `EmulatorPacket` (TLV `0x01`) rides NArk's `Extension`, wire-format-tested. **The full pipeline is PROVEN live** (`CovenantSpendTests` on regtest): a VTXO whose only leaf is `<tweakedEmulatorKey> CHECKSIGVERIFY <serverKey> CHECKSIG` was funded from a self-custody wallet and spent via `EmulatorClient.SubmitTxAsync` with the packet revealing the script — the emulator co-signed a passing script and **refused a failing one**. Covenant enforcement is script execution, not goodwill.
3. ~~**Breeding**~~ **DONE**: covenant-mode breeding is live (`BreedAuthorized` tapleaf — parents retained, child controlled by the species, fee paid, child metadata root oracle-signed by the game key via CHECKSIGFROMSTACK; honest breed co-signed, cheats refused). Transfers stay client-signed (the owner's own asset spend needs no covenant).
4. ~~**Wagered matches**~~ **DONE**: `wager_escrow`-style per-party escrows are live — each side stakes into its own address (settle branches oracle-authorized, plus a timelocked refund leaf paying only that party). Client reclaims an abandoned stake by rebuilding the covenant locally (`EscrowRefundFlow`), the liveness goal the pre-built-PSBT model targeted.
5. ~~**Item offers**~~ **DONE**: resting item-offer VTXOs are live — the seller rests one item unit behind a `fulfill` (pay-seller-the-ask) + timelocked `reclaim` covenant; ANY buyer funds the ask from their OWN wallet and takes the item (emulator refuses underpayment). Server is the discovery index only. Hero sales remain the parking-lot extension.
6. **Oracle retirement**: replace outcome/genome oracles with in-script derivation + VRF entropy per the ArkadeKitties design doc, as compiler/emulator capabilities allow.
7. **Upstream contributions to NArk**: the emulator client, packet builder, `ArkScriptHash` tweak, and covenant bytecode builders generalize beyond this game.

**Shipped since this roadmap was written (as of 2026-07-09):** the covenant surface now spans FIVE escrows, all STRUCTURALLY enforced (asset/tx introspection opcodes — not oracle-outcome trust) with a trustless timelocked reclaim on each: **wager**, **merge** (burn two heroes → mint one trait-concentrated fused hero — `MergeAuthorized`), **breed** (parents retained, child bound by group-output), **hero/item offers** (fully oracle-less — pay-the-seller + conservation), and **hardcore death-match** (JOINT escrow, winner-takes-all + permadeath; covenant-staked gear routes to the winner; opt-in **trait-absorb** — on a provably-fair roll the winner RE-MINTS absorbing the loser's rarer traits, both heroes burned, proven live). The oracle is now a *verifiable relay* of only off-chain-computable facts (fight winner, genome), each client-recomputable. A hand-written typed **`ArkadeHeroes.Client.Sdk`** (12 resource facades over the HTTP API) is consumed by the console client + all tests. **Gate: 173 unit + 50 E2E green.** Filed upstream: [arkade-os/arkd#1146](https://github.com/arkade-os/arkd/issues/1146) (timelocked-txid poisoning).

**Shipped since, and not described above (2026-09-05).** This document still reads as a breed-and-duel game; the modes that exist now are **gauntlet** (PvE ladder, the only XP mint), **trials** (endless affix ladder, scored not XP-bearing), **duel** (wagered 1v1), **squad** (3v3), **death-match** (permadeath, optional trait-absorb), **tournaments** (bracket + rake), **seasons** (ranked ladder + prize pool), **breeding/stud/merge**, **marketplace offers and bids**, **daily rewards**, and **achievements**. There is a full **Blazor WASM frontend** (`src/ArkadeHeroes.Web`) alongside the console client, with an in-browser non-custodial wallet. Progression is governed by `GameConfig`, whose verification-critical half (genome, rarity, affinity, curve, combat) is shared client+server at compile time and version-stamped onto every outcome, so retuning it is a coordinated release rather than a config edit. Balance is measurable rather than asserted — see `tools/ArkadeHeroes.Sim`. **Gate: 1068 unit + 140 bUnit + 59 E2E green.**

## 3. Topology

```
ArkadeHeroes.Client (console)      ArkadeHeroes.Web (Blazor WASM + in-browser wallet)
        │                                   │
        └──── REST (ArkadeHeroes.Shared DTOs, via ArkadeHeroes.Client.Sdk) ────┘
                              │
                    ArkadeHeroes.Server (ASP.NET minimal API)
        │ ArkadeHeroes.Core (rules)   ArkadeHeroes.Chain (IChainService)
        │                                   ├── InMemoryChainService (unit tests, offline dev)
        │                                   └── NArkChainService → arkd :7070 (regtest denigiri)
```

Regtest bring-up: `node external/dotnet-sdk/regtest/regtest.mjs start --profile ark --profile emulator`. The emulator is no longer optional — every covenant path (breed, merge, wager, death-match, offers) needs it to co-sign, and CI brings up both profiles.

## 4. Trust model summary

| Concern | Now | Target |
|---|---|---|
| Hero existence/ownership | on-chain (Arkade asset, player wallet) | same |
| Genome derivation | server, commit–reveal auditable + client-recomputed | covenant + oracle/VRF |
| Match outcome | server-computed, commit–reveal auditable + client-replayable. **Staked matches settle through the covenant escrow — emulator-enforced, not a treasury payout** (see Fees/wagers); the server's role is to relay the oracle signature for the branch the replay already proves | oracle replaced by in-script derivation + VRF entropy |
| Progression (XP/levels) | **signed receipts, player-held, replayable by anyone** (`ReceiptVerifier`; server DB is a cache) | + XP as on-chain asset deliveries |
| Fees/wagers | fees are client-paid invoices, on-chain verified. **A wagered match is covenant-ONLY: per-match escrow (seed commitment baked in), player-staked, emulator-settled — the treasury never holds a stake, and the custodial mode is refused rather than defaulted to**; settle branches are **oracle-authorized** (per-branch CHECKSIGFROMSTACK message signed by the game key) | + oracle messages derived from receipts |
| Wager liveness (abandoned match) | **per-party escrows with timelocked refund leaves — after expiry the staker reclaims with no oracle, no counterparty, no server** (CLTV tapleaf; submit-once after chain time passes expiry — see contracts/README timelock invariants). **Player-facing: client `refund <matchId>` rebuilds the contracts locally from server-published params and spends from the player's own wallet** | watchtower-friendly pre-built claims |
| Custody | player self-custody of heroes + funds (embedded wallet) | same, plus unilateral exit |
| Covenant liveness | **no covenant leaf has a unilateral exit** — every leaf ends `<operatorKey> OP_CHECKSIG`, so an operator that refuses to co-sign strands anything sitting in an escrow (stake, item, hero). Neither operator nor emulator can move it alone, so this is liveness, not theft | a CSV exit leaf on each contract, as `ArkPaymentContract` has |
| Rule enforcement | **the emulator, not Bitcoin consensus** — Arkade Script rides an OP_RETURN packet and the emulator signs only a passing script; the tapleaf itself is a plain 2-key multisig | unchanged until the opcodes are consensus-enforced; the gain is that the rule is public and byte-pinned |
