# Arkade Heroes

A breeding + battling game powered by [Arkade](https://docs.arkadeos.com) covenant mechanics — CryptoKitties-style genetics with levels, skills, equipment, and PvP matches, settled over Bitcoin via Arkade VTXOs.

> Status: early foundation, playable on local regtest. Minimal client/server with real game logic; graphics intentionally absent.
>
> **Covenant-first**: every game action is specified as an Arkade Script covenant (`contracts/`) over transaction shapes the server already executes today — asset deltas, genesis-metadata genome commitments, commit–reveal randomness, pinned payout outputs. Enforcement migrates from "server policy + client audit" to emulator co-signing without changing any transaction. The covenant key-tweak primitive (`ArkadeScriptTweak`) and emulator client are live in `src/ArkadeHeroes.Chain/Covenants/`, and `/api/chain/info` surfaces the emulator's signer key.

## Layout

| Path | What |
|---|---|
| `src/ArkadeHeroes.Core` | Pure domain: genome, breeding, stats, combat, progression, equipment |
| `src/ArkadeHeroes.Shared` | DTOs shared by server and client |
| `src/ArkadeHeroes.Chain` | Chain abstraction: in-memory chain for tests + NArk (Arkade .NET SDK) backend |
| `src/ArkadeHeroes.Server` | ASP.NET Core minimal API — the game service |
| `src/ArkadeHeroes.Client` | Console client — the "minimal layer" UI |
| `contracts/` | **Arkade Script covenants — the authoritative game rules** (breeding/transfer, wager escrow, item offers); see `contracts/README.md` |
| `tests/` | Unit + integration tests; regtest E2E |
| `external/dotnet-sdk` | [NArk](https://github.com/arkade-os/dotnet-sdk) submodule (brings `regtest/` denigiri harness) |
| `docs/research/` | Findings from the Arkade ecosystem repos this design is grounded in |
| `docs/DESIGN.md` | Game + covenant design |

## Getting started (offline, in-memory chain)

```bash
git clone --recursive <this repo>
dotnet build ArkadeHeroes.slnx
dotnet test tests/ArkadeHeroes.Tests
# server (in-memory chain simulation)
dotnet run --project src/ArkadeHeroes.Server
# client (separate terminal)
dotnet run --project src/ArkadeHeroes.Client
```

Client commands: `register <name>` → `starter` → `mine` → `breed 1 2` → `fight 3 1` → `shop` / `buy rusty-blade` / `equip 3 rusty-blade` / `unequip 3 Weapon`, plus `transfer <hero> <playerId>` and wagered PvP: `challenge <mine> <theirs> <sats>` → (opponent) `accept <matchId>` → `duel <matchId>`. Items are fungible Arkade assets (one unit backs one equipped hero). Every breed and fight is audited locally (commit–reveal + battle replay) and prints `fairness ✓`.

**Non-custodial:** the server never holds player keys. You register your own wallet's Arkade address; every fee/stake is an invoice your wallet pays (the server verifies receipt on-chain); heroes and items are minted straight into your wallet; transfers are spends you sign. In the offline InMemory mode a simulated wallet stands in (dev-only endpoints); in NArk mode the E2E suite drives real `SelfCustodyWallet`s and the console client prints invoices to pay until its embedded wallet lands.

## Playing on regtest with real Arkade assets

Heroes become real Arkade assets (amount 1, genome sealed in genesis metadata, controlled by the game's species asset); fees are real Arkade transactions.

```bash
# 1. Start the regtest stack (Docker + Node ≥18) — arkade-regtest@master submodule
#    at regtest/; .env.regtest (auto-discovered) remaps postgres/nbxplorer host
#    ports to avoid local collisions. Includes the Arkade Script emulator (:7073).
node regtest/regtest.mjs start --profile ark --profile emulator

# 2. Run the game server in NArk mode
#    (bash)        Chain__Mode=NArk dotnet run --project src/ArkadeHeroes.Server
#    (PowerShell)  $env:Chain__Mode="NArk"; dotnet run --project src/ArkadeHeroes.Server

# 3. Fund the treasury (address: GET http://localhost:5210/api/chain/info, also in server logs)
node regtest/regtest.mjs ark send --to <treasury tark1…> --amount 200000 --password secret

# 4. Play. Fund your player address (shown at register) the same way before breeding/shopping.
dotnet run --project src/ArkadeHeroes.Client
```

If `ark send` reports "not enough funds", the faucet's VTXOs expired — renew with `node regtest/regtest.mjs ark settle --password secret`.

The regtest E2E (`dotnet test tests/ArkadeHeroes.Tests.E2E`) runs this whole loop — mint, on-chain asset verification, breed, fight, shop — against live arkd and requires the stack from step 1.
