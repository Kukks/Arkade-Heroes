# NArk (arkade-os/dotnet-sdk) — integration notes

Read 2026-07-03 directly from the submodule at `external/dotnet-sdk` (pinned at tag `NArk.Abstractions/1.0.339-beta`). The E2E suite `NArk.Tests.End2End` is the authoritative consumption pattern; `samples/NArk.Wallet` (Blazor Client + Gateway + Shared) is the app-shaped reference.

## Regtest harness (denigiri)

Nested submodule `external/dotnet-sdk/regtest` (= `arkade-os/arkade-regtest`, no nigiri dependency, plain Docker + Node ≥18):

```bash
node external/dotnet-sdk/regtest/regtest.mjs start            # full stack
node external/dotnet-sdk/regtest/regtest.mjs start --profile ark        # base + arkd only
node external/dotnet-sdk/regtest/regtest.mjs mine [n]
node external/dotnet-sdk/regtest/regtest.mjs faucet <addr> <btc> [--confirm]
node external/dotnet-sdk/regtest/regtest.mjs ark <args...>    # ark client CLI in the arkd container
node external/dotnet-sdk/regtest/regtest.mjs arkd <args...>   # arkd server CLI (e.g. `arkd note --amount 100000000`)
node external/dotnet-sdk/regtest/regtest.mjs clean
```

Endpoints (host): arkd `http://localhost:7070` (gRPC+REST), NBXplorer `:32838`, Esplora REST `http://localhost:3000/api/`, emulator `:7073`, mempool web `:3000`. `SharedArkInfrastructure` ([NArk.Tests.End2End/SharedArkInfrastructure.cs](../../external/dotnet-sdk/NArk.Tests.End2End/SharedArkInfrastructure.cs)) health-checks `GET /v1/info` on arkd and instructs `node regtest/regtest.mjs start --profile boltz,delegate` when absent.

Auto-miner mines every 600s by default (`AUTOMINE_INTERVAL`); block-denominated locktimes (<512) enable "mine-to-expire" fast tests but require `AUTOMINE_INTERVAL=0`.

## Wallet bootstrap pattern (from `FundedWalletHelper.cs`)

```csharp
var transport = new GrpcClientTransport("http://localhost:7070");
var info = await transport.GetServerInfoAsync();                 // SignerKey, UnilateralExit, Network, Dust

var walletProvider = new InMemoryWalletProvider(transport);
var walletId = await walletProvider.CreateTestWallet();

var vtxoSync = new VtxoSynchronizationService(vtxoStorage, transport, [vtxoStorage, contractStorage]);
await vtxoSync.StartAsync(ct);

var contractService = new ContractService(walletProvider, contractStorage, transport);
var signer = await (await walletProvider.GetAddressProviderAsync(walletId))!.GetNextSigningDescriptor();
var contract = new ArkPaymentContract(info.SignerKey, info.UnilateralExit, signer);
await contractService.ImportContract(walletId, contract);
var address = contract.GetArkAddress();                          // fund via arkd note / ark send
```

Test funding: `DockerHelper.SendArkdNoteTo(address, sats)` (arkd note → instant offchain funds, no on-chain tx). Storage: `TestStorage` in `NArk.Tests.End2End/TestPersistance` shows the in-memory `IVtxoStorage`/`IContractStorage`/`IIntentStorage` impls; `NArk.Storage.EfCore` is the persistent option. There is also generic-host DI (`AddArk()`, `NArk.Core/Hosting`).

## Assets — the hero primitive (from `AssetTests.cs` + `AssetTestHelpers.cs`)

```csharp
var assetManager = new AssetManager(vtxoStorage, contracts, coinService, walletProvider,
    contractService, transport, new DefaultCoinSelector(), safetyService, intentStorage, []);

// Mint (metadata is committed at genesis — genome lives here)
var result = await assetManager.IssueAsync(walletId,
    new IssuanceParams(Amount: 1, ControlAssetId: species, Metadata: new() { ["genome"] = hex }));
// result.AssetId, result.ArkTxId

// Transfer: a normal spend whose output carries the asset
await spendingService.Spend(walletId, [
    new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, destinationArkAddress)
        { Assets = [new ArkTxOutAsset(assetId, 1)] }]);

// Burn
await assetManager.BurnAsync(walletId, new BurnParams(assetId, amount));

// Query
var details = await transport.GetAssetDetailsAsync(assetId);     // Supply etc.
```

- `IssuanceParams(ulong Amount, string? ControlAssetId = null, Dictionary<string,string>? Metadata = null)` (`NArk.Abstractions/Assets/IAssetManager.cs:10`). `ControlAssetId` = the ArkadeKitties species-control concept.
- VTXOs carry `Assets: [{AssetId, Amount}]`; balance = sum over unspent VTXOs.
- `CoinService` needs `PaymentContractTransformer` + `HashLockedContractTransformer`.
- Assets **survive batch settlement** (`AssetTests.AssetsSurviveBatchSettlement`).
- Sync: `vtxoSync.PollScriptsForVtxos(scripts)` and poll-until helpers (asset indexing lags `SubmitTx` by a moment).

## Vocabulary (repo CLAUDE.md, applies to all our Arkade-facing text)

"Arkade" not bare "Ark"; "batch" not "round"; code identifiers `Ark*`/`NArk*` are fine and must not be renamed.

## PRs

Only `pr-154` was open at research time (e2e config chore — nothing to adopt). The submodule pin already contains assets, batches, delegation, swaps, recovery, and signer-rotation support.
