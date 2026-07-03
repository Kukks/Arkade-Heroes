# Arkade Heroes

A breeding + battling game powered by [Arkade](https://docs.arkadeos.com) covenant mechanics — CryptoKitties-style genetics with levels, skills, equipment, and PvP matches, settled over Bitcoin via Arkade VTXOs.

> Status: early foundation. Minimal client/server with real game logic; graphics intentionally absent. The goal is a playable loop on local regtest first, deepening the covenant integration iteration by iteration.

## Layout

| Path | What |
|---|---|
| `src/ArkadeHeroes.Core` | Pure domain: genome, breeding, stats, combat, progression, equipment |
| `src/ArkadeHeroes.Shared` | DTOs shared by server and client |
| `src/ArkadeHeroes.Chain` | Chain abstraction: in-memory chain for tests + NArk (Arkade .NET SDK) backend |
| `src/ArkadeHeroes.Server` | ASP.NET Core minimal API — the game service |
| `src/ArkadeHeroes.Client` | Console client — the "minimal layer" UI |
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

Client commands: `register <name>` → `starter` → `mine` → `breed 1 2` → `fight 3 1` → `shop` / `equip 3 rusty-blade`. Every breed and fight is audited locally (commit–reveal + battle replay) and prints `fairness ✓`.

## Playing on regtest with real Arkade assets

Heroes become real Arkade assets (amount 1, genome sealed in genesis metadata, controlled by the game's species asset); fees are real Arkade transactions.

```bash
# 1. Start the denigiri regtest stack (Docker + Node ≥18).
#    .env.regtest remaps postgres/nbxplorer host ports to avoid collisions.
cd external/dotnet-sdk && node regtest/regtest.mjs start --profile ark --env ../../.env.regtest && cd ../..

# 2. Run the game server in NArk mode
#    (bash)        Chain__Mode=NArk dotnet run --project src/ArkadeHeroes.Server
#    (PowerShell)  $env:Chain__Mode="NArk"; dotnet run --project src/ArkadeHeroes.Server

# 3. Fund the treasury (address: GET http://localhost:5210/api/chain/info, also in server logs)
cd external/dotnet-sdk && node regtest/regtest.mjs ark send --to <treasury tark1…> --amount 200000 --password secret

# 4. Play. Fund your player address (shown at register) the same way before breeding/shopping.
dotnet run --project src/ArkadeHeroes.Client
```

The regtest E2E (`dotnet test tests/ArkadeHeroes.Tests.E2E`) runs this whole loop — mint, on-chain asset verification, breed, fight, shop — against live arkd and requires the stack from step 1.
