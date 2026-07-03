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
/// Arkade-backed chain service under the non-custodial mandate: the server
/// holds ONLY its treasury keys. Players are registered Arkade addresses;
/// their wallets live client-side. Fees/stakes are invoices at fresh treasury
/// sub-addresses the client pays; mints, item deliveries, and payouts are
/// treasury-signed outputs to the player's address; ownership and holdings
/// are read from the chain by script.
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
    private readonly SemaphoreSlim _treasurySpendLock = new(1, 1);
    private string? _treasuryWalletId;
    private string? _treasuryAddress;
    private string? _speciesAssetId;

    /// <summary>
    /// Treasury spends are serialized and retried: consecutive game actions
    /// (payout → item delivery → mint) each spend treasury VTXOs, and the SDK
    /// briefly locks coins per spend ("AlreadyLockedVtxo … try again later").
    /// </summary>
    private async Task<T> WithTreasurySpendAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _treasurySpendLock.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex) when (attempt < 4 && IsTransientSpendError(ex))
                {
                    // Coin locks are time-limited (1 min TTL, never explicitly
                    // released on failure) and every failed attempt re-locks part
                    // of the set — rapid retries livelock. On a lock error, go
                    // QUIET for longer than the TTL so the whole set expires.
                    var quiet = ex.GetType().Name.Contains("AlreadyLockedVtxo")
                        ? TimeSpan.FromSeconds(70)
                        : TimeSpan.FromSeconds(3 * attempt);
                    logger.LogWarning("Treasury spend transiently failed (attempt {Attempt}) — waiting {Quiet} then retrying: {Message}",
                        attempt, quiet, ex.Message);
                    await Task.Delay(quiet, ct);
                    await PollTreasuryScriptsAsync(ct);
                }
            }
        }
        finally
        {
            _treasurySpendLock.Release();
        }
    }

    /// <summary>
    /// Spend failures that heal on retry: coins locked by a just-finished spend,
    /// or coin selection landing on a subdust-change combination because a
    /// fresh change/payment VTXO hasn't synced into local storage yet.
    /// </summary>
    private static bool IsTransientSpendError(Exception ex)
        => ex.GetType().Name.Contains("AlreadyLockedVtxo")
           || ex.Message.Contains("change address should be specified", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Consolidates the treasury's plain-BTC coins into a single VTXO when
    /// fragmentation builds up (invoice payments arrive as many small coins).
    /// Sum-in == sum-out, so there is no change output at all — immune to the
    /// subdust-change dead-end that poisons coin locks. Keeping one large coin
    /// also makes issuance auto-selection trivially clean.
    /// </summary>
    private async Task ConsolidateTreasuryBtcIfNeededAsync(CancellationToken ct)
    {
        var coins = await spendingService.GetAvailableCoins(_treasuryWalletId!, ct);
        var btcCoins = coins.Where(c => c.Assets is null or { Count: 0 }).ToArray();
        if (btcCoins.Length < 3) return;

        var total = btcCoins.Sum(c => c.TxOut.Value.Satoshi);
        var serverInfo = await transport.GetServerInfoAsync(ct);
        if (total < serverInfo.Dust.Satoshi) return;

        var selfContract = await contractService.DeriveContract(_treasuryWalletId!, NextContractPurpose.SendToSelf, cancellationToken: ct);
        await spendingService.Spend(_treasuryWalletId!, btcCoins,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(total), selfContract.GetArkAddress()),
        ], cancellationToken: ct);
        logger.LogInformation("Consolidated {Count} treasury BTC coins into one ({Total} sats)", btcCoins.Length, total);
        await PollTreasuryScriptsAsync(ct);
    }

    /// <summary>
    /// Deterministic inputs for asset deliveries: the coin carrying the asset
    /// plus the treasury's LARGEST plain-BTC coin. Auto-selection can pick tiny
    /// invoice-payment coins and dead-end in subdust change, which then leaves
    /// coins locked — a large change output avoids the whole failure class.
    /// </summary>
    private async Task<global::NArk.Abstractions.ArkCoin[]> SelectDeliveryCoinsAsync(string assetId, CancellationToken ct)
    {
        var coins = await spendingService.GetAvailableCoins(_treasuryWalletId!, ct);
        var assetCoin = coins.FirstOrDefault(c => c.Assets?.Any(a => a.AssetId == assetId) == true)
            ?? throw new InvalidOperationException($"Treasury does not hold asset {assetId} — issuance may still be syncing.");
        var btcCoin = coins
            .Where(c => c.Assets is null or { Count: 0 } && c.Outpoint != assetCoin.Outpoint)
            .OrderByDescending(c => c.TxOut.Value.Satoshi)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Treasury has no plain BTC coin to fund the delivery — fund the treasury address.");
        return [assetCoin, btcCoin];
    }

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
                address = await DeriveTreasuryAddressAsync(walletId, ct);
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

    private async Task<string> DeriveTreasuryAddressAsync(string walletId, CancellationToken ct)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive, cancellationToken: ct);
        return contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
    }

    private async Task<string> EnsureSpeciesAssetAsync(CancellationToken ct)
    {
        await EnsureTreasuryAsync(ct);
        if (_speciesAssetId is not null) return _speciesAssetId;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_speciesAssetId is not null) return _speciesAssetId;

            var balance = await SumTreasurySatsAsync(ct);
            if (balance == 0)
                throw new InvalidOperationException(
                    $"Treasury wallet has no funds — send sats to {_treasuryAddress} " +
                    "(regtest: node regtest/regtest.mjs ark send --to <address> --amount <sats> --password secret) " +
                    "so the species control asset can be issued.");

            var issuance = await assetManager.IssueAsync(_treasuryWalletId!,
                new IssuanceParams(Amount: 1, ControlAssetId: null,
                    Metadata: new Dictionary<string, string>
                    {
                        ["name"] = "Arkade Heroes Species",
                        ["game"] = "arkade-heroes",
                    }), ct);

            await WaitForTreasuryAssetAsync(issuance.AssetId, TimeSpan.FromSeconds(30), ct);

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

    // ── IChainService: info ────────────────────────────────────────────

    private string? _emulatorSignerKey;
    private bool _emulatorProbed;

    public async Task<ChainInfo> GetInfoAsync(CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);

        if (!_emulatorProbed)
        {
            _emulatorProbed = true;
            try
            {
                var emulator = new Covenants.EmulatorClient(new Uri(options.EmulatorUri.TrimEnd('/') + "/"));
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                _emulatorSignerKey = (await emulator.GetInfoAsync(probeCts.Token)).SignerPubkey;
                logger.LogInformation("Arkade Script emulator reachable at {Uri}, signer {Key}",
                    options.EmulatorUri, _emulatorSignerKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Arkade Script emulator not reachable at {Uri} ({Reason}) — covenant paths unavailable until it is",
                    options.EmulatorUri, ex.Message);
            }
        }

        return new ChainInfo("NArk", serverInfo.Network.Name, _treasuryAddress!, _speciesAssetId, _emulatorSignerKey);
    }

    // ── Players = addresses ────────────────────────────────────────────

    public async Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        ArkAddress.Parse(arkadeAddress); // throws on malformed input

        if (await GetKvAsync($"addressOwner:{arkadeAddress}", ct) is { } existing && existing != playerId)
            throw new InvalidOperationException("Address already registered to another player.");

        await SetKvAsync($"address:{playerId}", arkadeAddress, ct);
        await SetKvAsync($"addressOwner:{arkadeAddress}", playerId, ct);
        logger.LogInformation("Player {PlayerId} registered self-custody address {Address}", playerId, arkadeAddress);
    }

    public async Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default)
        => await GetKvAsync($"address:{playerId}", ct)
           ?? throw new InvalidOperationException($"Player {playerId} has no registered address.");

    public async Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default)
    {
        var vtxos = await GetVtxosAtPlayerAddressAsync(playerId, ct);
        return vtxos.Aggregate(0L, (sum, v) => sum + (long)v.Amount);
    }

    // ── Fees: invoices at fresh treasury sub-addresses ─────────────────

    public async Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var contract = await contractService.DeriveContract(_treasuryWalletId!, NextContractPurpose.Receive, cancellationToken: ct);
        var arkAddress = contract.GetArkAddress();
        var address = arkAddress.ToString(serverInfo.Network == Network.Main);
        var script = arkAddress.ScriptPubKey.ToHex();

        var invoiceId = $"inv-{Guid.NewGuid():N}";
        await SetKvAsync($"invoiceScript:{invoiceId}", script, ct);
        await SetKvAsync($"invoiceAmount:{invoiceId}", amountSats.ToString(), ct);
        logger.LogInformation("Invoice {InvoiceId} ({Memo}): {Amount} sats → {Address}", invoiceId, memo, amountSats, address);
        return new FeeInvoice(invoiceId, address, amountSats, memo);
    }

    public async Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default)
    {
        var script = await GetKvAsync($"invoiceScript:{invoiceId}", ct);
        var amountText = await GetKvAsync($"invoiceAmount:{invoiceId}", ct);
        if (script is null || amountText is null) return false;
        var required = long.Parse(amountText);
        if (required == 0) return true;

        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
        var vtxos = await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct);
        var received = vtxos.Aggregate(0L, (sum, v) => sum + (long)v.Amount);
        return received >= required;
    }

    // ── Treasury-signed actions ────────────────────────────────────────

    public async Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default)
    {
        var species = await EnsureSpeciesAssetAsync(ct);
        var playerAddress = await GetPlayerAddressAsync(toPlayerId, ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var metadata = data.ToMetadata();
        metadata["game"] = "arkade-heroes";

        var issuance = await WithTreasurySpendAsync(async () =>
        {
            await ConsolidateTreasuryBtcIfNeededAsync(ct);
            var result = await assetManager.IssueAsync(_treasuryWalletId!,
                new IssuanceParams(Amount: 1, ControlAssetId: species, Metadata: metadata), ct);
            await WaitForTreasuryAssetAsync(result.AssetId, TimeSpan.FromSeconds(30), ct);
            return result;
        }, ct);

        await WithTreasurySpendAsync(async () => await spendingService.Spend(_treasuryWalletId!,
            await SelectDeliveryCoinsAsync(issuance.AssetId, ct),
            [
                new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(playerAddress))
                {
                    Assets = [new ArkTxOutAsset(issuance.AssetId, 1)],
                },
            ], cancellationToken: ct), ct);

        logger.LogInformation("Minted hero asset {AssetId} (gen {Generation}) → {PlayerId} @ {Address}",
            issuance.AssetId, data.Generation, toPlayerId, playerAddress);
        return new HeroMintResult(issuance.AssetId, issuance.ArkTxId);
    }

    private const ulong ItemIssuanceSupply = 1_000;

    public async Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default)
    {
        var species = await EnsureSpeciesAssetAsync(ct);
        var playerAddress = await GetPlayerAddressAsync(toPlayerId, ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var kvKey = $"itemAsset:{itemId}";
        var assetId = await GetKvAsync(kvKey, ct);
        if (assetId is null)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                assetId = await GetKvAsync(kvKey, ct);
                if (assetId is null)
                {
                    try
                    {
                        var issuance = await WithTreasurySpendAsync(async () =>
                        {
                            await ConsolidateTreasuryBtcIfNeededAsync(ct);
                            var result = await assetManager.IssueAsync(_treasuryWalletId!,
                                new IssuanceParams(ItemIssuanceSupply, species, new Dictionary<string, string>
                                {
                                    ["item"] = itemId,
                                    ["name"] = itemName,
                                    ["game"] = "arkade-heroes",
                                }), ct);
                            await WaitForTreasuryAssetAsync(result.AssetId, TimeSpan.FromSeconds(30), ct);
                            return result;
                        }, ct);
                        assetId = issuance.AssetId;
                        await SetKvAsync(kvKey, assetId, ct);
                        logger.LogInformation("Item asset issued for {ItemId}: {AssetId} (supply {Supply})",
                            itemId, assetId, ItemIssuanceSupply);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"item-issuance[{itemId}]: {ex.Message}", ex);
                    }
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        try
        {
            var txId = await WithTreasurySpendAsync(async () => await spendingService.Spend(_treasuryWalletId!,
                await SelectDeliveryCoinsAsync(assetId, ct),
                [
                    new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(playerAddress))
                    {
                        Assets = [new ArkTxOutAsset(assetId, 1)],
                    },
                ], cancellationToken: ct), ct);
            logger.LogInformation("Item {ItemId} unit delivered to {PlayerId}: {TxId}", itemId, toPlayerId, txId);
            return new ItemDeliveryResult(assetId, txId.ToString());
        }
        catch (Exception ex) when (ex.Message.StartsWith("item-issuance") == false)
        {
            throw new InvalidOperationException($"item-delivery[{itemId}→{toPlayerId}]: {ex.Message}", ex);
        }
    }

    public async Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        if (amountSats <= 0) return "free";
        var playerAddress = await GetPlayerAddressAsync(toPlayerId, ct);

        var txId = await WithTreasurySpendAsync(() => spendingService.Spend(_treasuryWalletId!,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ArkAddress.Parse(playerAddress)),
        ], cancellationToken: ct), ct);
        logger.LogInformation("Payout {Amount} sats ({Memo}) → {PlayerId}: {TxId}", amountSats, memo, toPlayerId, txId);
        return txId.ToString();
    }

    // ── On-chain reads at the player's address ─────────────────────────

    public async Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default)
    {
        var vtxos = await GetVtxosAtPlayerAddressAsync(playerId, ct);
        return vtxos.Any(v => v.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId));
    }

    public async Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default)
    {
        var assetId = await GetKvAsync($"itemAsset:{itemId}", ct);
        if (assetId is null) return 0;
        var vtxos = await GetVtxosAtPlayerAddressAsync(playerId, ct);
        return vtxos
            .Where(v => v.Assets is { Count: > 0 })
            .SelectMany(v => v.Assets!)
            .Where(a => a.AssetId == assetId)
            .Aggregate(0UL, (sum, a) => sum + a.Amount);
    }

    private async Task<IReadOnlyCollection<ArkVtxo>> GetVtxosAtPlayerAddressAsync(string playerId, CancellationToken ct)
    {
        var address = await GetPlayerAddressAsync(playerId, ct);
        var script = ArkAddress.Parse(address).ScriptPubKey.ToHex();
        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
        return await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct);
    }

    // ── Treasury sync helpers ──────────────────────────────────────────

    private async Task<long> SumTreasurySatsAsync(CancellationToken ct)
    {
        var vtxos = await vtxoStorage.GetVtxos(walletIds: [_treasuryWalletId!], cancellationToken: ct);
        return vtxos.Aggregate(0L, (sum, v) => sum + (long)v.Amount);
    }

    private async Task PollTreasuryScriptsAsync(CancellationToken ct)
    {
        var contracts = await contractStorage.GetContracts(walletIds: [_treasuryWalletId!], cancellationToken: ct);
        foreach (var contract in contracts)
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { contract.Script });
    }

    private async Task WaitForTreasuryAssetAsync(string assetId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var vtxos = await vtxoStorage.GetVtxos(walletIds: [_treasuryWalletId!], cancellationToken: ct);
            if (vtxos.Any(v => v.Assets is { Count: > 0 } assets && assets.Any(a => a.AssetId == assetId)))
                return;
            await PollTreasuryScriptsAsync(ct);
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Asset {assetId} did not appear in the treasury wallet within {timeout}.");
    }
}
