using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NBitcoin;

namespace ArkadeHeroes.Chain.NArk;

/// <summary>
/// Arkade-backed chain service (NArk SDK): heroes are Arkade assets (amount 1,
/// genome in genesis metadata, control = the game's species asset), player
/// funds are VTXOs, and fees are Arkade transactions to the treasury.
///
/// v1 custody model: the game server manages HD wallets for the treasury and
/// each player (coinflip-style house mode, extended to player keys). Moving
/// player keys client-side is a planned iteration; the IChainService surface
/// doesn't change.
/// </summary>
public class NArkChainService(
    IWalletStorage walletStorage,
    IVtxoStorage vtxoStorage,
    IContractStorage contractStorage,
    IContractService contractService,
    IAssetManager assetManager,
    SpendingService spendingService,
    global::NArk.Core.Transport.IClientTransport transport,
    VtxoSynchronizationService vtxoSync,
    IDbContextFactory<GameArkDbContext> dbFactory,
    NArkChainOptions options,
    ILogger<NArkChainService> logger) : IChainService
{
    private const string TreasuryWalletKey = "treasuryWalletId";
    private const string TreasuryAddressKey = "treasuryAddress";
    private const string SpeciesAssetKey = "speciesAssetId";

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _treasuryWalletId;
    private string? _treasuryAddress;
    private string? _speciesAssetId;

    // ── Bookkeeping KV ─────────────────────────────────────────────────

    private async Task<string?> GetKvAsync(string key, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.ChainKv.FindAsync([key], ct))?.Value;
    }

    private async Task SetKvAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.ChainKv.FindAsync([key], ct);
        if (row is null) db.ChainKv.Add(new GameChainKv { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }

    // ── Initialization ─────────────────────────────────────────────────

    private async Task EnsureTreasuryAsync(CancellationToken ct)
    {
        if (_treasuryWalletId is not null) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_treasuryWalletId is not null) return;

            var walletId = await GetKvAsync(TreasuryWalletKey, ct);
            if (walletId is null)
            {
                var serverInfo = await transport.GetServerInfoAsync(ct);
                var mnemonic = string.IsNullOrWhiteSpace(options.TreasuryMnemonic)
                    ? new Mnemonic(Wordlist.English, WordCount.Twelve).ToString()
                    : options.TreasuryMnemonic;
                var wallet = await WalletFactory.CreateWallet(mnemonic, null, serverInfo, ct);
                await walletStorage.SaveWallet(wallet, ct);
                walletId = wallet.Id;
                await SetKvAsync(TreasuryWalletKey, walletId, ct);
                logger.LogInformation("Created treasury wallet {WalletId}", walletId);
            }

            var address = await GetKvAsync(TreasuryAddressKey, ct);
            if (address is null)
            {
                address = await DeriveAddressAsync(walletId, ct);
                await SetKvAsync(TreasuryAddressKey, address, ct);
                logger.LogInformation("Treasury Arkade address: {Address} — fund this on regtest to enable minting", address);
            }

            _speciesAssetId = await GetKvAsync(SpeciesAssetKey, ct);
            _treasuryAddress = address;
            _treasuryWalletId = walletId;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Issues the species control asset (the ArkadeKitties species-gate) on
    /// first need. Requires the treasury to hold at least one funded VTXO.
    /// </summary>
    private async Task<string> EnsureSpeciesAssetAsync(CancellationToken ct)
    {
        await EnsureTreasuryAsync(ct);
        if (_speciesAssetId is not null) return _speciesAssetId;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_speciesAssetId is not null) return _speciesAssetId;

            var balance = await SumWalletSatsAsync(_treasuryWalletId!, ct);
            if (balance == 0)
                throw new InvalidOperationException(
                    $"Treasury wallet has no funds — send sats to {_treasuryAddress} " +
                    "(regtest: node regtest/regtest.mjs ark send --to <address> --amount <sats>) " +
                    "so the species control asset can be issued.");

            var issuance = await assetManager.IssueAsync(_treasuryWalletId!,
                new IssuanceParams(Amount: 1, ControlAssetId: null,
                    Metadata: new Dictionary<string, string>
                    {
                        ["name"] = "Arkade Heroes Species",
                        ["game"] = "arkade-heroes",
                    }), ct);

            await WaitForAssetVtxoAsync(_treasuryWalletId!, issuance.AssetId, TimeSpan.FromSeconds(30), ct);

            _speciesAssetId = issuance.AssetId;
            await SetKvAsync(SpeciesAssetKey, issuance.AssetId, ct);
            logger.LogInformation("Species control asset issued: {AssetId}", issuance.AssetId);
            return _speciesAssetId;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> DeriveAddressAsync(string walletId, CancellationToken ct)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive, cancellationToken: ct);
        return contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
    }

    // ── IChainService ──────────────────────────────────────────────────

    public async Task<ChainInfo> GetInfoAsync(CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);
        return new ChainInfo("NArk", serverInfo.Network.Name, _treasuryAddress!, _speciesAssetId);
    }

    public async Task<PlayerWallet> GetOrCreatePlayerWalletAsync(string playerId, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);

        var addressKey = $"address:{playerId}";
        if (await GetKvAsync(addressKey, ct) is { } existing)
            return new PlayerWallet(playerId, existing);

        var serverInfo = await transport.GetServerInfoAsync(ct);
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var wallet = await WalletFactory.CreateWallet(mnemonic, null, serverInfo, ct);
        await walletStorage.SaveWallet(wallet, ct);
        await SetKvAsync($"wallet:{playerId}", wallet.Id, ct);

        var address = await DeriveAddressAsync(wallet.Id, ct);
        await SetKvAsync(addressKey, address, ct);
        logger.LogInformation("Created player wallet {WalletId} for {PlayerId} at {Address}",
            wallet.Id, playerId, address);
        return new PlayerWallet(playerId, address);
    }

    private async Task<string> RequirePlayerWalletIdAsync(string playerId, CancellationToken ct)
        => await GetKvAsync($"wallet:{playerId}", ct)
           ?? throw new InvalidOperationException($"Player {playerId} has no chain wallet yet.");

    public async Task<long> GetBalanceSatsAsync(string playerId, CancellationToken ct = default)
    {
        var walletId = await RequirePlayerWalletIdAsync(playerId, ct);
        return await SumWalletSatsAsync(walletId, ct);
    }

    private async Task<long> SumWalletSatsAsync(string walletId, CancellationToken ct)
    {
        var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], cancellationToken: ct);
        return vtxos.Aggregate(0L, (sum, v) => sum + (long)v.Amount);
    }

    public async Task<HeroMintResult> MintHeroAssetAsync(string playerId, HeroMintData data, CancellationToken ct = default)
    {
        var species = await EnsureSpeciesAssetAsync(ct);
        var playerAddress = (await GetOrCreatePlayerWalletAsync(playerId, ct)).ArkadeAddress;
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var metadata = data.ToMetadata();
        metadata["game"] = "arkade-heroes";

        // 1. Treasury mints the hero (amount 1, species-controlled, genome sealed in genesis metadata).
        var issuance = await assetManager.IssueAsync(_treasuryWalletId!,
            new IssuanceParams(Amount: 1, ControlAssetId: species, Metadata: metadata), ct);
        await WaitForAssetVtxoAsync(_treasuryWalletId!, issuance.AssetId, TimeSpan.FromSeconds(30), ct);

        // 2. Deliver to the player: dust-carried asset output; change returns to treasury.
        await spendingService.Spend(_treasuryWalletId!,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(playerAddress))
            {
                Assets = [new ArkTxOutAsset(issuance.AssetId, 1)],
            },
        ], cancellationToken: ct);

        logger.LogInformation("Minted hero asset {AssetId} (gen {Generation}) → {PlayerId}",
            issuance.AssetId, data.Generation, playerId);
        return new HeroMintResult(issuance.AssetId, issuance.ArkTxId);
    }

    public async Task<string> PayFeeAsync(string playerId, long amountSats, string memo, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        if (amountSats <= 0) return "free";
        var walletId = await RequirePlayerWalletIdAsync(playerId, ct);

        var txId = await spendingService.Spend(walletId,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ArkAddress.Parse(_treasuryAddress!)),
        ], cancellationToken: ct);
        logger.LogInformation("Fee {Amount} sats ({Memo}) paid by {PlayerId}: {TxId}",
            amountSats, memo, playerId, txId);
        return txId.ToString();
    }

    public async Task<string> PayoutAsync(string playerId, long amountSats, string memo, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        if (amountSats <= 0) return "free";
        var playerAddress = (await GetOrCreatePlayerWalletAsync(playerId, ct)).ArkadeAddress;

        var txId = await spendingService.Spend(_treasuryWalletId!,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ArkAddress.Parse(playerAddress)),
        ], cancellationToken: ct);
        logger.LogInformation("Payout {Amount} sats ({Memo}) → {PlayerId}: {TxId}",
            amountSats, memo, playerId, txId);
        return txId.ToString();
    }

    public async Task<string> TransferHeroAssetAsync(string fromPlayerId, string toPlayerId, string assetId, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var fromWalletId = await RequirePlayerWalletIdAsync(fromPlayerId, ct);
        var toAddress = (await GetOrCreatePlayerWalletAsync(toPlayerId, ct)).ArkadeAddress;
        var serverInfo = await transport.GetServerInfoAsync(ct);

        // Make sure the sender's asset VTXO is in local storage before coin selection.
        await WaitForAssetVtxoAsync(fromWalletId, assetId, TimeSpan.FromSeconds(30), ct);

        var txId = await spendingService.Spend(fromWalletId,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(toAddress))
            {
                Assets = [new ArkTxOutAsset(assetId, 1)],
            },
        ], cancellationToken: ct);
        logger.LogInformation("Hero asset {AssetId} transferred {From} → {To}: {TxId}",
            assetId, fromPlayerId, toPlayerId, txId);
        return txId.ToString();
    }

    public async Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default)
    {
        var walletId = await RequirePlayerWalletIdAsync(playerId, ct);
        await PollWalletScriptsAsync(walletId, ct);
        var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], cancellationToken: ct);
        return vtxos.Any(v => v.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId));
    }

    // ── Sync helpers (poll pattern from the SDK's E2E asset helpers) ───

    private async Task PollWalletScriptsAsync(string walletId, CancellationToken ct)
    {
        var contracts = await contractStorage.GetContracts(walletIds: [walletId], cancellationToken: ct);
        foreach (var contract in contracts)
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { contract.Script });
    }

    private async Task WaitForAssetVtxoAsync(string walletId, string assetId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId], cancellationToken: ct);
            if (vtxos.Any(v => v.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId)))
                return;
            await PollWalletScriptsAsync(walletId, ct);
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Asset {assetId} did not appear in wallet {walletId} within {timeout}.");
    }
}
