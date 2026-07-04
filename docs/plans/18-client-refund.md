# Task 18 — client `refund <matchId>`: player-facing timelocked stake reclaim

**Status: SHIPPED.** Implemented substantially as specced below (two deltas: `/api/chain/info` advertises the existing `Chain:NArk:EmulatorUri`/`EsploraUri` options rather than a new `EsploraApiUri` key — the mempool web proxy `:3000/api` serves the esplora endpoints incl. `mediantime`; and the dev refund endpoint maps `InvalidOperationException` → `GameRuleException` for 400s). Gate: 69 unit + 7 E2E green (`CovenantRefundTests` ×4, `ClientRefundFlowTests`). This file remains as the design record.
**Goal:** a player whose covenant-match counterparty vanished reclaims their stake from the console client with **no oracle, no counterparty, no server cooperation** — completing the liveness story of task 17 (commit `bd901fa`) end-user-side.

**Prerequisite reading:** `contracts/README.md` → *Timelock protocol invariants*. Invariant #4 (submit-once; arkd poisoned-txid bug) shapes this whole design: **a single refused submission of the canonical refund tx permanently destroys that refund path** on current arkd. The client must therefore gate on the *chain's* clock and submit exactly once.

## Validated facts this design rests on (directed tests run 2026-07-04)

1. arkd exposes **no public chain-time RPC** (grepped `arkd/api-spec/protobuf` — `GetCurrentBlockTime` exists only on the internal wallet service).
2. The regtest stack's mempool backend serves an esplora-compatible REST API on **`http://localhost:8999/api/v1`**; `GET /blocks/tip/hash` → hash, `GET /block/{hash}` → JSON **containing `mediantime`** (verified live; note the `/api/v1` prefix — plain `/api/...` 404s).
3. NArk's `EsploraBlockchain.GetChainTime` (external/dotnet-sdk/NArk.Core/Blockchain/EsploraBlockchain.cs:40) already returns **MedianTime** as the timestamp. It issues *relative* requests (`blocks/tip/hash`), so if you reuse it the `HttpClient.BaseAddress` **must end with a trailing slash** (`http://localhost:8999/api/v1/`) or .NET drops the last path segment. A small owned helper (two GETs) is also acceptable and avoids the footgun.
4. MTP is the **safe** gate under either arkd blocktime semantic: consensus requires tip time > MTP, so `mediantime ≥ expiry ⇒ tip ≥ expiry`. (`RegtestHelper.GetMedianTimeAsync`/`WaitForChainTimeAsync` already exist for tests — `tests/ArkadeHeroes.Tests.E2E/RegtestHelper.cs`.)
5. The refund covenant (`ArkadeCovenants.RefundTo`) pays ONLY the party's own pinned address — a malicious/lying server can at worst make the spend fail; it can never redirect funds. So the client may consume server-provided params, but must rebuild the contract itself and locate its own stake VTXO (trustless verification by construction).

## Implementation plan (file by file)

### 1. `src/ArkadeHeroes.Chain/Covenants/WagerEscrowContracts.cs` — NEW (shared builder)

Extract the escrow construction from `NArkChainService` so server and client derive **byte-identical** contracts:

- Promote the private record `NArkChainService.EscrowParams` (src/ArkadeHeroes.Chain/NArk/NArkChainService.cs:452) to a public
  `WagerEscrowParams(string CommitmentHex, string ChallengerAddress, string DefenderAddress, long StakeSats, string OraclePkHex, string MatchId, long RefundAfterUnixSeconds)`
  — **keep the property names identical**: the params are persisted as JSON in KV `escrow:{matchId}` and System.Text.Json matches by name, so existing rows stay readable.
- `public static (ArkadeArtifactContract Challenger, ArkadeArtifactContract Defender) Build(WagerEscrowParams p, OutputDescriptor operatorKey, string emulatorSignerKeyHex)` — move the body of `BuildEscrowContractsAsync` (NArkChainService.cs:470-509) verbatim: two settle branches (`SettleAuthorized` with per-branch `SettleMessage`) + per-party `refund` function with `LockTime((uint)p.RefundAfterUnixSeconds)`. `operatorKey` is `serverInfo.SignerKey` (an `OutputDescriptor` — see `ArkadeArtifactContract` ctor, ArkadeArtifactContract.cs:38).
- Refactor `NArkChainService` to call it (its async wrapper keeps fetching serverInfo/emulator key, then delegates). Delete the private record; `using ArkadeHeroes.Chain.Covenants;` gains `WagerEscrowParams`.
- Namespace imports needed: `NArk.Abstractions` (ArkAddress), `NBitcoin`, `NBitcoin.Scripting` (OutputDescriptor).

*Verify:* `dotnet build` + the two `WagerEscrowCovenantTests` E2Es still pass (they construct contracts inline — unchanged — but `FullGameLoopOnRegtestTests` exercises the server path end-to-end).

### 2. `IChainService.GetWagerEscrowParamsAsync(string matchId, CancellationToken)` → `Task<WagerEscrowParams?>`

- **NArk**: deserialize KV `escrow:{matchId}` (see `RequireEscrowParamsAsync`, NArkChainService.cs:538) — return null when absent.
- **InMemory** (src/ArkadeHeroes.Chain/InMemoryChainService.cs:144-166): the sim `Escrow` record currently **drops `RefundAfterUnixSeconds` and the commitment** (`CreateWagerEscrowAsync` receives them, stores neither). Extend the record: `Escrow(ChallengerId, DefenderId, StakeSats, OraclePkHex, CommitmentHex, RefundAfterUnixSeconds)`. Return params with the players' registered sim addresses (`GetPlayerAddressAsync`) and the sim data. MatchId = key.

### 3. InMemory refund semantics + dev endpoint

- `InMemoryChainService.RefundEscrowFromPlayer(string playerId, string matchId)` (mirror of `StakeEscrowFromPlayer`, InMemoryChainService.cs:173): party check ("Not a party"), `Settled` → refuse ("already settled"), that party's `…Staked` must be true → else "nothing staked"; **time gate**: `DateTimeOffset.UtcNow.ToUnixTimeSeconds() < RefundAfterUnixSeconds` → refuse ("locked until …") — this simulates FORFEIT_CLOSURE_LOCKED; then flip the party's `Staked=false` and credit `StakeSats` back to `_playerBalances`. Double-refund is inherently refused by the flag flip.
- `POST /api/dev/refund-escrow {MatchId}` in `Program.cs` next to `/api/dev/stake-escrow` (same InMemory-only guard + player-header pattern).

### 4. Server surface

- `GET /api/matches/{id}/escrow` → `chain.GetWagerEscrowParamsAsync`; 404 when null (invoice-mode matches or unknown). Return the record as-is (public params; the escrow address commits to them — nothing secret).
- `ChainInfoDto` (src/ArkadeHeroes.Shared/Dtos.cs:147) gains `string? EmulatorUri = null, string? EsploraApiUri = null` (appended optional params — wire-compatible). `Program.cs:218` fills them from chain options/config (`Chain__EmulatorUri`; new `Chain__EsploraApiUri`, default `http://localhost:8999/api/v1` on regtest). InMemory mode leaves both null.

### 5. `src/ArkadeHeroes.Chain/Covenants/EscrowRefundFlow.cs` — NEW (client-side, also the E2E's entry point)

```
public static async Task<EmulatorSubmitResponse> RefundAsync(
    SelfCustodyWallet wallet, Uri emulatorUri, WagerEscrowParams p,
    Func<CancellationToken, Task<long>> chainMedianTime, CancellationToken ct = default)
```
1. `serverInfo` from the wallet's transport; emulator key from `EmulatorClient(emulatorUri).GetInfoAsync()` — both fetched independently of the game server (trustless).
2. `WagerEscrowContracts.Build(p, serverInfo.SignerKey, emulatorInfo.SignerPubkey)`.
3. Party detection: `wallet.Address == p.ChallengerAddress` → challenger contract; `== p.DefenderAddress` → defender; neither → throw ("this wallet is not a party").
4. Locate the stake: `CovenantSpender.WaitForVtxosAsync(wallet, myContract, 1, short timeout)` filtered to `Amount == p.StakeSats`; none → throw ("no stake VTXO at the escrow — nothing to refund, or already settled/refunded").
5. **Gate:** `await chainMedianTime(ct) >= p.RefundAfterUnixSeconds` else throw a dedicated `RefundNotYetDueException(dueUnix, chainNowUnix)` — the caller reports "due at X, chain time Y, retry later". **Never submit before this passes** (invariant #4). Read-only checks are always safe to repeat.
6. Submit **once** — no retry loop, no catch-and-resubmit:
   `CovenantSpender.SpendAsync(wallet, emulatorUri, myContract, "refund", [ArkadeCovenants.EncodeIndex(0)], stakeVtxo, [new TxOut(Money.Satoshis(p.StakeSats), ArkAddress.Parse(wallet.Address).ScriptPubKey)])`
   with `CovenantInput.LockTime = new LockTime((uint)p.RefundAfterUnixSeconds)` — use `SpendManyAsync` with the explicit `CovenantInput` since `SpendAsync` doesn't take a locktime (check CovenantProbe.cs:61; extend `SpendAsync` or call `SpendManyAsync` directly — prefer the latter, zero API churn).
7. Chain-time source helper: `EsploraChainTime.GetMedianTimeAsync(HttpClient, baseUrl)` — two GETs (`{base}/blocks/tip/hash`, `{base}/block/{hash}`, parse `mediantime`); ~15 lines, avoids the NArk BaseAddress trailing-slash footgun.

### 6. Client REPL command

`GameClient.cs` command switch (~line 188): `case "refund": await RefundAsync(Arg(parts, 1, "refund <matchId>")); break;`
- Mode branch like invoices (GameClient.cs:91/103): **InMemory** → `POST /api/dev/refund-escrow`; **NArk** → `GET /api/matches/{id}/escrow` + `/api/chain/info` (EmulatorUri, EsploraApiUri; allow env overrides `ARKADE_HEROES_EMULATOR`/`ARKADE_HEROES_ESPLORA`), then `EscrowRefundFlow.RefundAsync` with the wallet; on `RefundNotYetDueException` print the due/now times; on success `WaitForBalanceAsync` and print the reclaimed amount.

### 7. Tests

Unit (InMemory, `tests/ArkadeHeroes.Tests` — extend `CovenantMatchTests`):
- escrow params endpoint round-trips what `CreateWagerEscrowAsync` stored (incl. commitment + refundAfter — the fields the current sim DROPS; this test pins the §2 fix),
- refund before expiry → refused; after expiry (configure `Game__WagerEscrowRefundAfter=00:00:01` via env or a small options override) → player balance restored, match escrow no longer "funded",
- double refund → refused; non-party → refused; refund after settle → refused.

E2E (regtest, new `tests/ArkadeHeroes.Tests.E2E/ClientRefundFlowTests.cs`, follow `FullGameLoopOnRegtestTests` server-boot pattern — **env vars, not WebApplicationFactory config overrides**: `Chain__Mode=NArk`, `Game__WagerEscrowRefundAfter=00:00:08`):
1. two funded `SelfCustodyWallet`s; alice opens a covenant match against bob's hero; alice stakes to `open.EscrowAddress` (challenger); **bob never accepts/stakes**;
2. `GET /api/matches/{id}/escrow` → params; assert `WagerEscrowContracts.Build(params, …).Challenger.GetArkAddress()` equals the address alice paid (the trustless-rebuild property, asserted explicitly);
3. `RegtestHelper.WaitForChainTimeAsync(params.RefundAfterUnixSeconds, 120s)` (mines until MTP ≥ expiry);
4. `EscrowRefundFlow.RefundAsync(aliceWallet, emulatorUri, params, esploraMedianTime)` — exactly one call;
5. `aliceWallet.WaitForBalanceAsync(before + stake, 90s)`.
Also assert the pre-expiry path refuses **via the flow's own gate** (call before mining: expect `RefundNotYetDueException` — no tx submitted, nothing poisoned; do NOT probe arkd with the canonical tx).

### Gate & wrap-up

Full gate = **65+ unit, 7+ E2E** (6 existing + ClientRefundFlowTests; expect the unit count to grow with the new InMemory tests). Then: commit (verify `git show --stat`), update `contracts/README.md` (client refund flow note under the escrow bullet), `docs/DESIGN.md` trust-model row (client refund shipped), auto-memory, `TaskUpdate` #18 → completed, proceed to `docs/plans/19-backlog.md`.

## Pitfalls specific to this task

- **Do not add any retry around the submit** (step 6). If it fails, surface the error; a retry loop here re-creates the exact poisoned-txid failure task 17 spent a day diagnosing. The refusal-probe pattern (non-canonical locktime `expiry+1`) exists in `WagerEscrowCovenantTests.AbandonedStakeIsRefundableAfterExpiry_WithoutTheServer` if a test needs to prove pre-expiry refusal at the arkd level.
- The E2E's server does its own `IsEscrowFundedAsync` polling; a half-funded escrow (alice only) keeps `/fight` refusing — that's expected and irrelevant to the refund.
- After alice's refund, her stake VTXO is spent; the match record still exists (marking matches cancelled/expired server-side is OUT of scope — noted in backlog).
- `SelfCustodyWallet.Address` is the wallet's primary address (`ChainKv` `primaryAddress`) — the same one `RegisterAsync` sends the server, so party detection by string equality is sound. If paranoid, compare `ArkAddress.Parse(...).ScriptPubKey` instead of strings.
