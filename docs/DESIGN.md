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
| 16–31 | Reserved, zero in v1 (future trait-map versions) |

Growth genes are the breeding meta: invisible at level 1, dominant at high level, so lineages matter more than stat screens.

### Breeding

`GeneMixer.Mix(parentA, parentB, entropy)` mirrors the ArkadeKitties design doc's `mixGenomes`: per-trait crossover selected by entropy bytes (even → A, odd → B), mutation when the selector byte ≥ 248 (1/32 per trait) rerolled from `SHA256(A ‖ B ‖ entropy)`. Generation = `max(parents) + 1`. Cooldown doubles per breed (cap 2⁷) scaled by the cooldown gene.

**Entropy is commit–reveal**: server commits `SHA256(serverSeed)` when the breed request opens; the player supplies a nonce; entropy = `SHA256(serverSeed ‖ parentAId ‖ parentBId ‖ nonce)`. Seed + nonce are stored with the child (and in its asset metadata), so anyone can re-derive the genome and audit the server. This is the same trust posture as coinflip's secrets and maps 1:1 onto the future covenant version.

### Progression

- XP: winner `60 + 12·loserLevel`, loser `20 + 4·winnerLevel`. Curve `80 + 45·level^1.35`, cap 50.
- Skills: level 1 `Strike`; level 3 gene-A skill; level 6 gene-B skill; level 9 `Elemental Burst`.
- Equipment: 3 slots (weapon/armor/trinket), fixed catalog, flat stat mods, priced in sats. Each item type is a **fungible Arkade asset** (issued lazily by the treasury on first sale, supply 1000, species-controlled, item id in genesis metadata). Buying pays the price and delivers one unit to the player's wallet; equipping allocates a held unit (one unit backs at most one equipped hero); unequip frees it. Loadouts don't travel on hero transfer — item assets stay with the seller's wallet, so the server strips equipment when a hero changes owners. Banco-style item trading is the v2 path.

### Combat

Deterministic auto-battler (`BattleEngine.Fight(a, b, matchSeed)`): initiative by speed, skill choice by highest expected damage off cooldown, damage = `power · scale / (defense + 25)` with element ring multipliers (1.3×/0.75×), crit (luck), dodge (speed), ±10% variance — all rolls from a seeded xoshiro256** stream. Max 60 turns, then HP-fraction decision. Output is a replayable `BattleResult` event log; the match seed is commit–reveal over both players' nonces, so either player can re-run the engine and verify the outcome.

## 2. Chain integration (ArkadeHeroes.Chain) — covenant-first

**The covenants in `contracts/*.ark` are the authoritative rules of the game.** Every mechanic is specified as an Arkade Script covenant over a concrete transaction shape; the running server is an *executor* of those shapes, not the definition of them. Migrating a mechanic to covenant enforcement swaps who refuses an invalid transaction (the emulator's tweaked-key signature instead of server policy) — the transactions, asset structures, and derivations do not change.

| Mechanic | Covenant (contracts/) | Transaction shape (live today) | Enforcement today | Covenant gap |
|---|---|---|---|---|
| Hero identity | `arkade_heroes.ark` | asset amount 1, species-controlled, genome+provenance in genesis metadata | on-chain (structure) + server issuance | compile + tapleaf binding |
| Breeding | `arkade_heroes.breed` | parents retained (Δ0) + control retained (Δ0) + child fresh-minted (Δ1) with derived genome | server executes; commit–reveal audited client-side | emulator co-signing + oracle sig |
| Transfer | `arkade_heroes.transfer` | Δ0 asset move to recipient's VTXO | on-chain (asset move) via server wallets | owner-key spend path |
| Wagered match | `wager_escrow.ark` | two stake VTXOs → atomic sweep to winner; time-locked forfeit/refund | treasury escrow + on-chain payout | escrow taptree + emulator packet |
| Item sales | `item_offer.ark` | pay-seller-in-same-tx for one asset unit | server delivers after fee payment | offer VTXO (banco pattern) |

**Invariants that keep every mode covenant-compatible** (enforced by tests):
1. All randomness is commit–reveal (`SHA256(serverSeed)` published before player nonces) and all derivations are pure functions — `GeneMixer.Mix` is byte-compatible with the design-doc `mixGenomes`, `BattleEngine.Fight` replays from the match seed.
2. Genome, generation, lineage, and the commit–reveal proof are sealed in asset **genesis metadata** — the covenant's `metadataHash` checks bind to data that already exists on-chain today.
3. Heroes/items are **species-controlled asset groups** with the exact delta discipline the covenants require (retain Δ0 / fresh-mint Δ1).
4. Fees, stakes, and payouts are plain Arkade transactions with fixed destinations — the outputs the covenants pin (`scriptPubKey == SingleSig(...)`, `value >= pot`).

**Covenant plumbing already in code** (`src/ArkadeHeroes.Chain/Covenants/`): `ArkadeScriptTweak` (the `ArkScriptHash` tagged-hash key tweak that binds a tapleaf to a script, ported from the emulator) and `EmulatorClient` (`/v1/info`, `/v1/tx`). The regtest stack's emulator is probed at startup and its signer key is surfaced through `/api/chain/info` — clients can compute covenant keys themselves.

### v1 execution mode — real assets, server-executed shapes

- **Hero = Arkade asset, amount 1**, minted via NArk `AssetManager.IssueAsync` with `Metadata = { genome, generation, parentA, parentB, serverSeed, nonce }` and `ControlAssetId` = the **species control asset** the game server mints once at first boot (the ArkadeKitties species-gate concept).
- **Canonicity**: a hero is legit iff its asset's control is the game's species asset (server is the only holder of the control asset, hence the only possible issuer — verifiable on-chain via `GetAssetDetailsAsync`).
- **Ownership**: the player's Arkade address holds the VTXO carrying the hero asset. Mint delivers the asset to the player; trades are plain asset spends.
- **Payments**: breeding fees / item purchases are Arkade transactions (sats) from player wallet to the game treasury address.
- **Transfers**: heroes move between players as plain asset spends (`POST /api/heroes/{id}/transfer`, client `transfer`).
- **Wagered matches**: open → accept → duel. Both sides escrow their stake with the treasury (real Arkade transactions); the winner's owner is paid the pot on resolution. This is the server-escrow stepping stone to the coinflip-style covenant escrow (`atomicSweep`) in v2.
- **Server wallet**: NArk `InMemoryWalletProvider` (EF Core later), funded on regtest via `arkd note`.
- The game DB caches hero state for matchmaking/progression; the chain is authoritative for existence + ownership.

### Covenant activation roadmap (contracts are written; this is the wiring order)

1. **Compiler wiring**: run `contracts/*.ark` through `arkade-os/compiler`, consume the artifact JSON (constructor args, per-function witness order, tapleaf asm) — see `contracts/README.md` for which constructs are already compile-verified vs. pending.
2. **Emulator Packet builder** (TLV `0x01` in the ARK extension OP_RETURN) on top of NArk's existing `Extension`/`Packet` encoders — the last missing piece between `EmulatorClient.SubmitTxAsync` and a real covenant spend.
3. **Breeding + transfer** move onto `arkade_heroes.ark` tapleaves (oracle = game key; emulator co-signs).
4. **Wagered matches** move onto `wager_escrow.ark` two-stake escrows with pre-built forfeit/refund PSBTs handed to clients at open/accept (coinflip's recovery model).
5. **Item offers** become resting `item_offer.ark` VTXOs — the shop works with the server offline, and the same covenant is the player-to-player marketplace.
6. **Oracle retirement**: replace outcome/genome oracles with in-script derivation + VRF entropy per the ArkadeKitties design doc, as compiler/emulator capabilities allow.
7. **Upstream contributions to NArk**: the emulator client, packet builder, `ArkScriptHash` tweak, and covenant bytecode builders generalize beyond this game.

## 3. Topology

```
ArkadeHeroes.Client (console)
        │ REST (ArkadeHeroes.Shared DTOs)
ArkadeHeroes.Server (ASP.NET minimal API)
        │ ArkadeHeroes.Core (rules)   ArkadeHeroes.Chain (IChainService)
        │                                   ├── InMemoryChainService (unit tests, offline dev)
        │                                   └── NArkChainService → arkd :7070 (regtest denigiri)
```

Regtest bring-up: `node external/dotnet-sdk/regtest/regtest.mjs start --profile ark` (add `emulator` when covenant work starts).

## 4. Trust model summary

| Concern | v1 | Target |
|---|---|---|
| Hero existence/ownership | on-chain (Arkade asset) | same |
| Genome derivation | server, commit–reveal auditable | covenant + oracle/VRF |
| Match outcome | server, commit–reveal auditable + client-replayable | covenant escrow, emulator-enforced |
| Fees/wagers | plain Arkade transactions | covenant escrow (atomicSweep) |
| Custody | player self-custody of heroes + funds | same, plus unilateral exit |
