# Engineering handoff — Arkade Heroes autonomous build

**Audience:** the next agent (Opus) continuing this build autonomously via /loop.
**Baseline:** commit `bd901fa`, clean working tree. Gates verified green 2026-07-04: **65/65 unit, 6/6 regtest E2E**.
**Read order:** this file → `contracts/README.md` (covenant traps — mandatory before touching chain code) → `docs/plans/18-client-refund.md` (the next task, fully specced) → `docs/DESIGN.md`.

---

## 1. Mission and standing directives

Build **Arkade Heroes**: a CryptoKitties-inspired breeding/battling game on Arkade (Bitcoin L2), in .NET, playable on local regtest, console UI only (graphics explicitly out of scope). The user's standing directives, in force until countermanded:

1. **No shortcuts. Loop until perfection.** Users must own their characters and progression **non-custodially**, and **covenants enforce consistent fairness** across the board. When a fix can be "server promises" or "covenant enforces", pick the covenant.
2. **Fast cadence**: self-schedule wakeups (~120s), maximize throughput within the Claude Max 20× 5-hour rate windows. If a window caps out, resume after reset.
3. **Commit locally per milestone** — never push (no remote exists). Verify every commit's contents with `git show --stat` (see §5 trap about `.gitignore`).
4. **Never skip, weaken, or filter out failing tests.** Fix root causes.
5. Covenant-first design: `contracts/*.ark` are the authoritative rules; runtime covenants are arg-free bytecode in `ArkadeCovenants` (see `contracts/README.md` for why).

Loop protocol per iteration: keep the gate green → implement milestone → full gate (65+ unit, 6+ E2E) → commit (verify with `git show --stat`) → update `contracts/README.md` + `docs/DESIGN.md` + auto-memory → `TaskUpdate` the task list → `ScheduleWakeup` ~120s with a precise continuation prompt (include: where you stopped, next steps, and "if the user has returned with new instructions, follow those instead").

## 2. Current state — what is live and proven (all on regtest unless noted)

- **Full non-custodial game loop**: register (player = their own Arkade address) → mint starter heroes (amount-1 Arkade assets, genome in immutable genesis metadata) → breed (commit–reveal randomness; client-side `FairnessAudit` recomputes) → level/skills/equipment (items = fungible Arkade assets) → invoice-mode wagered matches (client pays real invoices from an embedded self-custody wallet) → hero transfers (client-signed).
- **Covenant enforcement pipeline** (emulator co-signing): script-tweaked leaves (`ArkadeScriptTweak`, tag `ArkScriptHash`), `EmulatorPacket` TLV in the ARK extension OP_RETURN, `CovenantSpender` multi-input spends. Proven: passing scripts co-signed, failing refused.
- **Covenant-mode wagered matches in the game flow**: per-match, **per-party** escrow contracts (params persisted in KV `escrow:{matchId}`), players stake from their own wallets, fight gated on both escrows funded, server settles via emulator.
- **Oracle-authorized settlement**: settle branches pin a per-branch message + the game oracle key via `OP_CHECKSIGFROMSTACK`; forged sig / cross-branch replay / wrong seed / short pot all refused live. Oracle key = the receipt-signing game key.
- **Timelocked covenant refunds (liveness)**: abandoned stake reclaimed after expiry with **no oracle, no counterparty, no server** — CLTV tapleaf. This nearly died on four distinct protocol traps; they are documented as the four **timelock invariants** in `contracts/README.md`. Do not write timelocked spends without reading them.
- **Portable progression receipts**: BIP340-signed match/breeding facts, player-held, independently replayable (`ReceiptVerifier`).
- All three `.ark` contracts compile with `arkadec` (artifacts committed under `contracts/build/`).

What is **not** yet built: covenant breeding, XP-as-assets, marketplace, leaderboard, wallet-file encryption. See §7. (Task 18 — the client `refund` command — SHIPPED after this document was first written: gate is now **69 unit + 7 E2E**; see `docs/plans/18-client-refund.md` for the design record and §6.)

## 3. World verification runbook — run this BEFORE any work

Expected outputs recorded 2026-07-04 at `bd901fa`. If any check fails, fix the world first — do not code against a broken baseline.

```bash
# 1. Repo state (clean tree; HEAD is bd901fa or a descendant — handoff-doc commits follow it)
git -C C:/Git/Arkade-Heroes log --oneline -8     # → task-18 + handoff commits, then bd901fa
git -C C:/Git/Arkade-Heroes status --short       # → (empty)

# 2. Regtest stack up? (start: `node regtest/regtest.mjs start --profile ark --profile emulator` from repo root)
docker ps --format '{{.Names}}' | grep -E '^(arkd|emulator|bitcoin|mempool_api)$'   # all four present
# arkd = ghcr.io/arkade-os/arkd:v0.9.9-rc.1, emulator = v0.0.3 (its /v1/info self-reports v0.0.1 — stale metadata, trust the image tag)

# 3. Unit gate (fast, no infra needed)
dotnet test tests/ArkadeHeroes.Tests --nologo    # → Passed! 69/69, ~3s

# 4. Full E2E gate (regtest must be up; runs SERIAL by design, ~2 min)
dotnet test tests/ArkadeHeroes.Tests.E2E --nologo   # → Passed! 7/7

# 5. Chain plumbing probes
node regtest/regtest.mjs rpc getblockcount                       # bitcoin-cli passthrough works
curl -s http://localhost:8999/api/v1/blocks/tip/hash             # esplora/mempool API up (port 8999)
# block JSON has "mediantime" — the client-side chain clock for timelocked refunds:
# curl -s http://localhost:8999/api/v1/block/$TIP | grep mediantime
```

Faucet: `node regtest/regtest.mjs ark send --to <arkade-addr> --amount N --password secret`. Faucet VTXOs expire (~1h); on "not enough funds" run `node regtest/regtest.mjs ark settle --password secret` and retry (RegtestHelper.ArkSend automates this).

Environment facts: Windows 11, dotnet SDK 10.0.301 (libs target net8.0 in the SDK, net10.0 for game + tests), Docker running, no git remote. Ports: arkd `:7070` (gRPC+REST), emulator `:7073`, esplora/mempool backend `:8999`, mempool web `:3000`. The user's btcpayservertests containers squat 39372/32838 → repo-root `.env.regtest` remaps `POSTGRES_PORT=39373` / `NBXPLORER_PORT=32839` (auto-discovered by regtest.mjs; do NOT pass `--env` to `ark`/`arkd` passthrough subcommands).

Research clones (read-only reference, shallow) at `C:/Git/Arkade-Heroes-research/`: `arkd` (v0.9.9-rc.1 + master fetched — source of truth for operator behavior), `emulator`, `compiler` (arkadec.exe built at `compiler/target/release/arkadec.exe`), `arkade-regtest`, `banco`, `coinflip`, `solver`, `ts-sdk`, `arkade-assets`, `covclaimd`, `asset-registry`, `dotnet-sdk`. The user's own working clones also live in `C:/Git/*` — **never modify anything outside `C:/Git/Arkade-Heroes` and the research dir**.

## 4. Architecture map

| Project | Role | Key files |
|---|---|---|
| `ArkadeHeroes.Core` | Pure domain (no I/O): genome, breeding, combat, progression, fairness | `GeneMixer`, `BattleEngine`, `CommitReveal`, `FairnessAudit`, `ReceiptVerifier` |
| `ArkadeHeroes.Shared` | DTOs for server⇄client | `Dtos.cs` (incl. `ChainInfoDto`, match DTOs) |
| `ArkadeHeroes.Chain` | Chain abstraction | `IChainService` (the seam), `InMemoryChainService` (simulation with the SAME semantics — real BIP340 checks etc.), `NArk/NArkChainService` (real backend), `NArk/SelfCustodyWallet` (player wallet: isolated ServiceProvider, EF sqlite, mnemonic in wallet DB), `Covenants/*` |
| `ArkadeHeroes.Server` | Minimal API game service | `Program.cs` (endpoints incl. InMemory-only `/api/dev/*`), `GameService`, `GameStore` (JSON persistence), `ReceiptSigner`, `GameOptions` |
| `ArkadeHeroes.Client` | Console REPL | `GameClient.cs` (embedded `SelfCustodyWallet` per player via `ARKADE_HEROES_HOME`) |

Covenant layer (`src/ArkadeHeroes.Chain/Covenants/`):
- `ArkadeCovenants` — covenant bytecode builders (byte-for-byte coinflip ports): `PayTo`, `AtomicSweep`, `Sha256Gate`, `CheckSigFromStackGate`, `SettleAuthorized`, `RefundTo`, `SettleMessage`, `EncodeIndex`.
- `ArkadeArtifactContract` — named contract; each function = one leaf `<tweak(emulatorKey, script)> CHECKSIGVERIFY <operatorKey> CHECKSIG`; `ArkadeContractFunction.LockTime` wraps the leaf in `CompositeTapScript(LockTimeTapScript, …)` (CLTV **on the tapleaf** — invariant #1).
- `CovenantSpender` — the spend pipeline: builds the Arkade tx via `TransactionHelpers.ArkTransactionBuilder.ConstructArkTransaction` (public; signs nothing), attaches the `EmulatorPacket` (witness = VM initial stack), rebuilds the ark-tx PSBT to carry any input locktime (invariant #2), submits to the emulator which co-signs + finalizes with arkd. `SpendManyCoreAsync` is service-level (works over the server's DI graph or a player wallet's). NOTE: the packet's per-entry vin indices assume the builder preserves input order — it does (ShuffleInputs=false; all multi-input settle E2Es implicitly prove it), but the builder's asset-packet remap path (TransactionHelpers.cs:165-203) rebuilds an Extension output from the asset packet ALONE — see the packet-composition hazard in `docs/plans/19-backlog.md` before ever mixing assets with covenant spends.
- `EmulatorPacket` — TLV `0x01`; **inner fields are Bitcoin CompactSize, outer TLV is LEB128** (trap: they coincide below 128 bytes).
- `ArkadeScriptTweak`, `EmulatorClient` (`:7073`, `/v1/info`, `/v1/tx`).

Server⇄chain patterns: treasury spends serialize through `WithTreasurySpendAsync` (quiet ≥70s backoff on `AlreadyLockedVtxo` — see §5), KV in `GameChainKv` table, escrow params JSON under `escrow:{matchId}`. InMemory mode mirrors every covenant rule in-process so unit tests exercise real semantics; dev-only endpoints (`/api/dev/pay-invoice`, `/api/dev/transfer-asset`, `/api/dev/stake-escrow`) simulate the client wallet.

Config via env vars (WebApplicationFactory overrides LOSE to appsettings — always use env vars in tests): `Chain__Mode=InMemory|NArk`, `Chain__ArkUri`, `Chain__EmulatorUri`, `Game__ReceiptKeyHex`, `Game__WagerEscrowRefundAfter` (TimeSpan, default 24h).

## 5. The traps — cost days; do not rediscover them

Covenant/protocol traps live in **`contracts/README.md`** (authoritative, incl. the four timelock invariants and the **arkd poisoned-txid bug**: a refused SubmitTx permanently poisons that deterministic txid's event stream — a later accepted resubmission finalizes at RPC level but its VTXOs are never created and the input stays spendable; verified present on arkd v0.9.9-rc.1 AND master, `internal/core/domain/offchain_tx.go` sticky-`Failed` replay guards; worth an upstream report). The rest:

1. **`.gitignore` `*.e2e`** (fixed, stay alert): the template pattern case-insensitively matched `tests/ArkadeHeroes.Tests.E2E/` — an entire project silently never committed. Negation lines exist now. **Always verify commits with `git show --stat`.**
2. **Treasury spend livelock**: NArk safety-locks spend inputs (`vtxo::{outpoint}`, 1-min TTL) and does NOT release them on failure; rapid retries re-lock partially → self-sustaining lock. Remedy: serialize treasury spends + quiet ≥70s on `AlreadyLockedVtxo` (already implemented in `WithTreasurySpendAsync`).
3. **Coin-selection subdust dead-end** ("change address should be specified (Uncolored)"): small invoice coins pollute the treasury. Remedy (implemented): consolidate treasury BTC (sum-in==sum-out, no change) before issuances + explicit asset+largest-BTC delivery coin selection.
4. **E2E must run serial** — `[assembly: CollectionBehavior(DisableTestParallelization = true)]`; parallel runs fight over the shared faucet (VTXO_ALREADY_SPENT).
5. **xunit 2.x `IAsyncLifetime`** wants `Task`, not `ValueTask`.
6. **`SHA256` ambiguity** with `NBitcoin.Secp256k1` — alias it.
7. **Shell quirks**: bash `VAR=x cmd1 | cmd2` applies the var to cmd1 only (export instead); MSYS mangles `ref:path` colons (use PowerShell for those); the Bash tool cwd can reset between calls — use absolute paths or `cd` within the same command.
8. **`dotnet test` swallows `Console.WriteLine`** — add `--logger "console;verbosity=detailed"` to see test diagnostics.
9. **arkd log level**: only warnings/errors surface; a silent gRPC method = success. The emulator's `finalizing tx` info line is emitted BEFORE it calls FinalizeTx; the outcome is only visible on failure (an error line `finalizing tx failed: …`) — no error after it means the finalize succeeded.
10. **Debug arsenal**: `docker logs emulator|arkd --since 10m`; `node regtest/regtest.mjs rpc <bitcoin-cli args>`; arkd REST indexer `http://localhost:7070/v1/indexer/vtxos?outpoints=<txid>:<vout>` (also `?scripts=`); esplora `:8999/api/v1`. When a spend "succeeds" but funds don't appear, interrogate the indexer by outpoint FIRST — it distinguishes "never created" from "created but filtered".
11. **Session quirk (not code)**: the remote permission stream ("Yep Anywhere") intermittently dies mid-turn — Write/Edit/Bash all fail with "Tool permission request failed: Error: Stream closed". Work already on disk survives. Remedy: end the turn with a precise `ScheduleWakeup` continuation prompt; the channel heals between turns. Never leave a milestone uncommitted longer than necessary.

## 6. Task 18: client `refund <matchId>` — SHIPPED

Implemented and gated (69 unit + 7 E2E). Design record: **`docs/plans/18-client-refund.md`**. The next task is covenant breeding — start from `docs/plans/19-backlog.md` §19 (pinned opcode semantics + probe list).

## 7. Remaining work — the MVP completion map

**`docs/plans/20-mvp-completion.md` is the authoritative remaining-work list**, with the
acceptance bar (a two-player MVP walkthrough E2E + human runbook) and per-item
definitions-of-done. Current position: task 19 (covenant breeding) rungs 1–3 PROVEN, rung 4
(`BreedAuthorized` composition) is next — pinned semantics and the byte-order rule live in
`docs/plans/19-backlog.md` §19. After breeding: XP-assets → item marketplace → leaderboard →
wallet encryption → warts → the walkthrough. Parking lot (do not start early): CI, upstream
arkd bug report, hero marketplace, VRF.

## 8. Working agreements recap

- Auto-memory lives at `C:\Users\evilk\.claude\projects\C--Git-Arkade-Heroes\memory\` (index `MEMORY.md` → `arkade-heroes-project.md`, `cadence-preference.md`). Keep it updated per milestone; it is the cross-session brain, but **this repo's docs are the durable source of truth** — anything load-bearing goes in the repo.
- Never add AI attribution to commits/PRs. No time estimates in any artifact. Match existing file style. Commit messages: dense, technical, capture the WHY and the traps (see `git log` for the house style).
- The user reads results asynchronously; close each turn with honest status: what ran, what's confirmed vs inferred, commit hashes.
- The harness task list (TaskList/TaskUpdate tools) tracks milestones — tasks #1–#17 completed, **#18 in_progress** (its description points at the spec file). Keep it in sync per milestone; if a fresh session has an empty task list, recreate #18 from `docs/plans/18-client-refund.md`.
