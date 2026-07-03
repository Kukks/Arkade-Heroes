# Covenant game patterns — coinflip, emulator, banco/solver/covclaimd

Extracted 2026-07-03 from read-only clones under `C:\Git\Arkade-Heroes-research` (repos: `ArkLabsHQ/coinflip`, `arkade-os/{emulator,banco,solver,covclaimd}`, `dotnet-sdk`). All claims supported by official Arkade-owned sources unless flagged. Everything below is proven on **regtest**.

## Coinflip game-session lifecycle (the reference Arkade game)

State machine `pending → resolved | expired` in a SQLite `games` table. Canonical map: `coinflip/docs/PROTOCOL.md`.

1. **Setup `/play`** (`coinflip/packages/server/src/trustless-game.ts` `handleTrustlessPlay`): client posts `playerPubkey, playerHash, playerChangeAddress`. Server generates `(creatorSecret, creatorHash)` (secret byte-length encodes the digit), reserves a house VTXO from a pool (`vtxo-pool.ts`), builds the escrow taptree, funds the house side. Emulator is mandatory at `/play`.
2. **Escrow** (`coinflip/packages/lib/src/script.ts` `CoinflipEscrowScript`): two per-party P2TR VTXOs, each an **8-leaf taptree** = 4 collaborative covenant leaves (win/win/forfeit/refund, arkd+emulator co-signed) mirrored by 4 CSV unilateral-exit leaves. Win/forfeit leaves carry an `atomicSweep` covenant; refund leaves don't.
3. **Win predicate** (`buildCoinflipConditionScript`): SHA256-check both secrets, read digits from `OP_SIZE`, player wins iff `(creatorDigit+playerDigit) mod n ∈ [lo,target)`.
4. **Settle `/commit`** (`handleTrustlessCommit`): client reveals `playerSecret`; server builds a 2-in/1-out covenant sweep paying the pot to the winner, attaches `ConditionWitness [creatorSecret, playerSecret]` + an EmulatorPacket per input, POSTs to the emulator. **Player signs nothing on the win path.**
5. **Forfeit/refund**: PSBTs pre-built at `/play` time and stashed client-side; client submits directly to the emulator after expiry (`claimForfeit`, `runAutoClaim`). Server runs recovery workers on a 120s timer (`recoverOrphanedHouseEscrows`, `reconcilePendingSweeps`).
6. **v0.4 joint pot** (`trustless-game-v4.ts`): collapses two escrows into one co-funded VTXO; `/api/v4/play`, `/cofund`, `/cofund-finalize`, `/reveal`; win leaves use `payTo(winner,pot)`.

## Emulator co-signing (covenant enforcement without new Bitcoin opcodes)

- Covenant leaf = ordinary N-of-N multisig where the **emulator's key is tweaked**: `tweaked = emulator_key + tagged_hash("ArkScriptHash", arkade_script)·G` (`emulator/pkg/arkade/tweak.go`).
- The Arkade Script bytecode is not in the tapscript; it's revealed via an **Emulator Packet** — TLV type `0x01` in an ARK extension OP_RETURN (`ARK`/`0x41524b` magic), one entry per input: `vin`, script bytes, witness blob (`emulator/README.md`; encoder `emulator/pkg/arkade/emulator_packet.go`).
- The emulator runs the script in its **Arkade VM** (Bitcoin Script superset: re-enabled `OP_CAT/OP_MUL/…` + introspection opcodes `OP_INSPECTINPUTVALUE 0xc9`, `OP_INSPECTOUTPUTVALUE 0xcf`, `OP_INSPECTOUTPUTSCRIPTPUBKEY 0xd1`, …) and signs **only if the predicate is true** — the signature is the proof (`emulator/README.md`, `emulator/pkg/arkade/opcode.go`).
- Covenant builders (`coinflip/packages/contract-workflows-prototype/src/covenants.ts`): `payTo(pkScript,amount)` pins one output; `atomicSweep(pkScript,amount,otherInputValue)` cross-checks `INSPECTINPUTVALUE` so one escrow can't be swept alone.
- Submission: POST `{arkTx, checkpointTxs}` (base64 PSBTs, camelCase JSON) to emulator REST `POST /v1/tx` → `{signedArkTx, signedCheckpointTxs}`. The emulator, not the operator, holds the covenant key; arkd only relays.

## Minimal moving parts for an Arkade game

1. **Operator (arkd)** — VTXO ledger, checkpoint co-signer, tx stream, indexer. Liveness-only trust.
2. **Emulator** — Arkade Script signing service (port 7073 gRPC+REST). Required for covenant spends. Liveness-only trust.
3. **Game server (house mode)** — house wallet, commit-reveal driver, VTXO pool/liability management, SQLite, recovery timers.
4. **Client wallets** — self-custodial per player; commit/reveal secrets; fund own escrows; independent forfeit/refund recovery.

All vendored in `arkade-regtest` (`node regtest.mjs start`).

## Escrow / swap patterns reusable for Arkade Heroes

- **banco** (`banco/README.md`, `banco/src/offer.ts`): non-interactive asset-for-payment escrow. Maker computes covenant over `(wantAsset, wantAmount, makerPkScript, optional ratio)`, funds a swap VTXO, embeds the offer as ARK extension TLV `0x03`, goes offline. Taker spends iff their tx pays the maker in the same Arkade transaction (`INSPECTOUTPUTVALUE(0) >= wantAmount` + `INSPECTOUTPUTSCRIPTPUBKEY(0) == makerPkScript`; asset variant via `FINDASSETGROUPBYASSETID` + `INSPECTOUTASSETLOOKUP`). Partial fills re-create the swap VTXO with a price-ratio covenant. Taptree = Fulfill / Cancel `CLTV` / Exit `CSV`. → Template for **equipment/hero trading** and open **match-wager escrows**.
- **solver** (`solver/README.md`): Go daemon subscribing to arkd's tx stream with a `Plugin{Match, Solve}` runtime; banco plugin fulfills offers via the emulator. → Pattern for a **matchmaker/auction-settler bot**.
- **covclaimd** (`covclaimd/README.md`): watches for preimage-gated VTXOs, decrypts an ECIES `ClaimPacket` (TLV `0x04` or private `RevealService.Reveal`), spends through the covenant with the revealed preimage. Holds no funds. → Pattern for a **trustless loot/reward claim server**.

## .NET port notes (NArk)

- arkd transport exists: gRPC (`NArk.Core/Transport/GrpcClient/`, protos `ark/v1/*`) and REST (`RestClientTransport` — `/v1/tx/submit`, `/v1/tx/finalize`, intents).
- Reusable: contract types (`ArkPaymentContract`, `HashLockedArkPaymentContract`, `VHTLCContract`, `ArkDelegateContract`, `GenericArkContract`), tapscript leaves (`CollaborativePathArkTapScript`, `UnilateralPathArkTapScript`, `NofNMultisigTapScript`, `HashLockTapScript`, `LockTimeTapScript`), ARK extension TLV encode/decode (`NArk.Core/Assets/{Extension,Packet,AssetPacketBuilder}.cs`), intents, batch/tree signing, EF Core storage.
- **Gaps (not present in NArk as of submodule pin)**: (a) emulator client (`POST /v1/tx`, `/v1/info`, `/v1/intent`, `/v1/finalization`); (b) Emulator Packet TLV `0x01` builder; (c) `ArkScriptHash` tagged-hash pubkey tweak; (d) Arkade Script covenant bytecode builders (`payTo`/`atomicSweep`, `OP_INSPECT*`). These are the v2+ work items for trustless Arkade Heroes matches.
- Codegen path: emulator ships proto (`emulator/api-spec/protobuf/emulator/v1/service.proto`) + OpenAPI — a .NET client can be generated. REST gateway uses camelCase JSON.
- Reference stack being ported: Node/Express/better-sqlite3 + `@arkade-os/sdk` server; Vue client. .NET equivalents: ASP.NET Core + EF Core + NArk (+ NArk.Swaps for cash-in/out).
