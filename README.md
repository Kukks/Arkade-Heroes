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

## Getting started

```bash
git clone --recursive <this repo>
dotnet build ArkadeHeroes.sln
dotnet test tests/ArkadeHeroes.Tests
# server
dotnet run --project src/ArkadeHeroes.Server
# client (separate terminal)
dotnet run --project src/ArkadeHeroes.Client
```

Regtest E2E uses the SDK's bundled `arkade-regtest` (denigiri) docker harness — see `docs/research/dotnet-sdk.md`.
