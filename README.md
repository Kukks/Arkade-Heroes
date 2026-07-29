# Arkade Heroes

A breeding + battling game powered by [Arkade](https://docs.arkadeos.com) covenant mechanics — CryptoKitties-style genetics with levels, skills, equipment, and PvP matches, settled over Bitcoin via Arkade VTXOs.

> Status: playable on local regtest — a Blazor WebAssembly frontend (breed, battle, trade, tournaments, death-matches, with an in-browser non-custodial wallet) alongside a console client, over covenant-enforced game logic. Not launched yet (regtest only).
>
> **Covenant-first**: every game action is specified as an Arkade Script covenant (`contracts/`) over transaction shapes the server already executes today — asset deltas, genesis-metadata genome commitments, commit–reveal randomness, pinned payout outputs. Enforcement migrates from "server policy + client audit" to emulator co-signing without changing any transaction. The covenant key-tweak primitive (`ArkadeScriptTweak`) and emulator client are live in `src/ArkadeHeroes.Chain/Covenants/`, and `/api/chain/info` surfaces the emulator's signer key.

## Layout

| Path | What |
|---|---|
| `src/ArkadeHeroes.Core` | Pure domain: genome, breeding, stats, combat, progression, equipment |
| `src/ArkadeHeroes.Shared` | DTOs shared by server and client |
| `src/ArkadeHeroes.Chain` | Chain abstraction: in-memory chain for tests + NArk (Arkade .NET SDK) backend |
| `src/ArkadeHeroes.Server` | ASP.NET Core minimal API — the game service |
| `src/ArkadeHeroes.Client.Sdk` | Typed .NET client for the game API — used by the console client and the tests |
| `src/ArkadeHeroes.Client` | Console client (a REPL over the SDK) |
| `src/ArkadeHeroes.Web` | **Blazor WebAssembly frontend** — the graphical arcade: in-browser non-custodial wallet, breed/battle/trade/tournaments/death-matches |
| `contracts/` | **Arkade Script covenants — the authoritative game rules** (breeding/transfer, wager escrow, item offers); see `contracts/README.md` |
| `tests/` | Unit + integration tests; regtest E2E |
| `external/dotnet-sdk` | [NArk](https://github.com/arkade-os/dotnet-sdk) submodule (brings `regtest/` denigiri harness) |
| `docs/research/` | Findings from the Arkade ecosystem repos this design is grounded in |
| `docs/DESIGN.md` | Game + covenant design |
| `docs/RECOVERY.md` | **Non-custodial recovery** — restore from seed, covenant refund/reclaim, unilateral exit |
| `docs/HANDOFF.md` | **Engineering handoff for the autonomous build** — verified state, runbook, traps, next-task specs (`docs/plans/`) |

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

**Non-custodial:** the server never holds player keys. In NArk mode the client opens an embedded self-custody wallet on first use (keys in a local SQLite next to your session; `backup` prints the mnemonic, `wallet` shows address/balance/assets, `fund` shows how to top up). Registration binds your wallet's address; every fee/stake is an invoice your wallet pays automatically (the server only verifies receipt on-chain); heroes and items are minted straight into your wallet; transfers are spends your wallet signs. Run several players side by side with `ARKADE_HEROES_HOME=<dir>`. In the offline InMemory mode a simulated wallet stands in (dev-only endpoints). Wallet-file encryption is a tracked follow-up.

### In the browser (Blazor WebAssembly)

The graphical UI is `src/ArkadeHeroes.Web` — the game with real hero art and animations, and a non-custodial wallet that boots in the browser (breed, battle, merge, trade, tournaments, death-matches, all driven client-side). With the server running, start the frontend in a separate terminal:

```bash
dotnet run --project src/ArkadeHeroes.Web    # browser UI at http://localhost:5132
```

Open http://localhost:5132 and create a wallet. The frontend calls the game API at `http://localhost:5210` by default (set `ApiBaseUrl` to change it), so start the server bound there — InMemory to explore offline, or `Chain__Mode=NArk` for live regtest (below), where you fund your in-browser wallet the same way as the treasury.

### In Docker

Server and frontend ship as two images — the server does **not** host the WASM bundle, so
they stay on separate origins (the server's CORS policy already allows this).

```bash
# The submodule is REQUIRED: both images build against external/dotnet-sdk, and without it
# the restore inside the container fails with MSB3202. Docker cannot fetch it for you.
git submodule update --init external/dotnet-sdk

cp .env.example .env      # then edit — see the treasury note below
docker compose up --build # frontend :5132, game API :5210
```

Every knob is an environment variable in `docker-compose.yml`, each commented with what it
does and its default. Two things worth knowing before a real deployment:

- **`Game__StateDbPath` must stay on the `arkade-state` volume.** It holds players, the hero
  roster and paid-but-unclaimed purchases. Off the volume, every container restart destroys
  them — and heroes are money-bearing assets, not cache.
- **`Chain__NArk__TreasuryMnemonic` is the treasury seed phrase — real bitcoin on mainnet.**
  It has no default and is never baked into an image; it is read from `.env`, which is
  gitignored and excluded from the Docker build context. Leave it empty unless restoring an
  existing treasury (empty = generate one on first boot and persist it to the volume).

Images are published to GHCR by `.github/workflows/docker-publish.yml`, tagged by commit SHA
and by branch/tag.

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

## Continuous integration

`.github/workflows/ci.yml` builds the whole solution and runs the unit/integration suite (in-memory chain, no regtest) on every push and PR to `main` — this is the gate that guards every change. The live-regtest E2E suite runs as a manual `workflow_dispatch` job that brings up the Arkade regtest stack; it's opt-in because it needs Docker and is slow (~2.5 min). The unit gate is validated locally with the same commands (`dotnet build ArkadeHeroes.slnx -c Release` → `dotnet test tests/ArkadeHeroes.Tests -c Release`).
