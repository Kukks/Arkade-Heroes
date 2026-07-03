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
- Equipment: 3 slots (weapon/armor/trinket), fixed catalog, flat stat mods, priced in sats. Items are id-addressed so a later iteration can issue them as fungible Arkade assets and trade them banco-style.

### Combat

Deterministic auto-battler (`BattleEngine.Fight(a, b, matchSeed)`): initiative by speed, skill choice by highest expected damage off cooldown, damage = `power · scale / (defense + 25)` with element ring multipliers (1.3×/0.75×), crit (luck), dodge (speed), ±10% variance — all rolls from a seeded xoshiro256** stream. Max 60 turns, then HP-fraction decision. Output is a replayable `BattleResult` event log; the match seed is commit–reveal over both players' nonces, so either player can re-run the engine and verify the outcome.

## 2. Chain integration (ArkadeHeroes.Chain)

### v1 (this iteration) — real assets, server-authoritative rules

- **Hero = Arkade asset, amount 1**, minted via NArk `AssetManager.IssueAsync` with `Metadata = { genome, generation, parentA, parentB, serverSeed, nonce }` and `ControlAssetId` = the **species control asset** the game server mints once at first boot (the ArkadeKitties species-gate concept).
- **Canonicity**: a hero is legit iff its asset's control is the game's species asset (server is the only holder of the control asset, hence the only possible issuer — verifiable on-chain via `GetAssetDetailsAsync`).
- **Ownership**: the player's Arkade address holds the VTXO carrying the hero asset. Mint delivers the asset to the player; trades are plain asset spends.
- **Payments**: breeding fees / item purchases are Arkade transactions (sats) from player wallet to the game treasury address.
- **Server wallet**: NArk `InMemoryWalletProvider` (EF Core later), funded on regtest via `arkd note`.
- The game DB caches hero state for matchmaking/progression; the chain is authoritative for existence + ownership.

### v2+ — progressive decentralization (documented, not built)

1. **Covenant breeding** (ArkadeKitties compiled example as baseline): breed becomes an Arkade Script contract — parents + species control retained (`delta == 0`), child fresh-minted (`delta == 1`), `metadataHash` verified, oracle sig → emulator co-signing. Our `GeneMixer` is already byte-compatible with the design doc's `mixGenomes` so the off-chain and on-chain derivations agree.
2. **Wagered matches**: coinflip's two-escrow `atomicSweep` taptree (4 collaborative + 4 exit leaves) with the match seed commit–reveal as the win predicate; forfeit/refund PSBTs pre-built for the client.
3. **Equipment marketplace**: banco non-interactive offers (asset↔sats, asset↔asset), optional solver bot as auction settler.
4. **NArk gaps to contribute upstream** (from research): emulator REST/gRPC client, Emulator Packet TLV `0x01` builder, `ArkScriptHash` tweak, covenant bytecode builders.

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
