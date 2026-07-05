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
    global::NArk.Abstractions.Safety.ISafetyService safetyService,
    IWalletProvider walletProvider,
    global::NArk.Abstractions.Intents.IIntentStorage intentStorage,
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

    private const ulong XpIssuanceSupply = 1_000_000_000;

    public async Task<ulong> DeliverXpAsync(string toPlayerId, ulong amount, CancellationToken ct = default)
    {
        if (amount == 0) return await GetXpBalanceAsync(toPlayerId, ct);
        var species = await EnsureSpeciesAssetAsync(ct);
        var playerAddress = await GetPlayerAddressAsync(toPlayerId, ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var assetId = await GetKvAsync("xpAsset", ct);
        if (assetId is null)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                assetId = await GetKvAsync("xpAsset", ct);
                if (assetId is null)
                {
                    var issuance = await WithTreasurySpendAsync(async () =>
                    {
                        await ConsolidateTreasuryBtcIfNeededAsync(ct);
                        var result = await assetManager.IssueAsync(_treasuryWalletId!,
                            new IssuanceParams(XpIssuanceSupply, species, new Dictionary<string, string>
                            {
                                ["asset"] = "xp",
                                ["game"] = "arkade-heroes",
                            }), ct);
                        await WaitForTreasuryAssetAsync(result.AssetId, TimeSpan.FromSeconds(30), ct);
                        return result;
                    }, ct);
                    assetId = issuance.AssetId;
                    await SetKvAsync("xpAsset", assetId, ct);
                    logger.LogInformation("XP asset issued: {AssetId} (supply {Supply})", assetId, XpIssuanceSupply);
                }
            }
            finally { _initLock.Release(); }
        }

        await WithTreasurySpendAsync(async () => await spendingService.Spend(_treasuryWalletId!,
            await SelectDeliveryCoinsAsync(assetId, ct),
            [
                new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(playerAddress))
                {
                    Assets = [new ArkTxOutAsset(assetId, amount)],
                },
            ], cancellationToken: ct), ct);
        logger.LogInformation("Delivered {Amount} XP to {PlayerId}", amount, toPlayerId);
        return await GetXpBalanceAsync(toPlayerId, ct);
    }

    public async Task<ulong> GetXpBalanceAsync(string playerId, CancellationToken ct = default)
    {
        var assetId = await GetKvAsync("xpAsset", ct);
        if (assetId is null) return 0;
        var vtxos = await GetVtxosAtPlayerAddressAsync(playerId, ct);
        return vtxos.Where(v => v.Assets is { Count: > 0 }).SelectMany(v => v.Assets!)
            .Where(a => a.AssetId == assetId).Aggregate(0UL, (s, a) => s + a.Amount);
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

    // ── Covenant wager escrows ─────────────────────────────────────────

    private async Task<string> RequireEmulatorSignerAsync(CancellationToken ct)
    {
        await GetInfoAsync(ct); // probes the emulator once
        return _emulatorSignerKey
               ?? throw new InvalidOperationException(
                   $"Covenant matches need the Arkade Script emulator at {options.EmulatorUri} — it is not reachable.");
    }

    /// <summary>
    /// Rebuilds both per-party escrow contracts from the persisted params via
    /// the shared <see cref="Covenants.WagerEscrowContracts"/> builder — the
    /// same construction clients run to reclaim refunds trustlessly.
    /// </summary>
    private async Task<(Covenants.ArkadeArtifactContract Challenger, Covenants.ArkadeArtifactContract Defender)>
        BuildEscrowContractsAsync(Covenants.WagerEscrowParams parameters, CancellationToken ct)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorKey = await RequireEmulatorSignerAsync(ct);
        return Covenants.WagerEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorKey);
    }

    public async Task<WagerEscrowInfo> CreateWagerEscrowAsync(
        string matchId, string challengerPlayerId, string defenderPlayerId,
        long stakeSats, byte[] seedCommitment32, string oraclePubKeyHex,
        long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var parameters = new Covenants.WagerEscrowParams(
            Convert.ToHexString(seedCommitment32).ToLowerInvariant(),
            await GetPlayerAddressAsync(challengerPlayerId, ct),
            await GetPlayerAddressAsync(defenderPlayerId, ct),
            stakeSats,
            oraclePubKeyHex,
            matchId,
            refundAfterUnixSeconds);

        await SetKvAsync($"escrow:{matchId}", System.Text.Json.JsonSerializer.Serialize(parameters), ct);

        var (challengerContract, defenderContract) = await BuildEscrowContractsAsync(parameters, ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var isMain = serverInfo.Network == Network.Main;
        var challengerAddress = challengerContract.GetArkAddress().ToString(isMain);
        var defenderAddress = defenderContract.GetArkAddress().ToString(isMain);
        logger.LogInformation("Wager escrow for {MatchId}: challenger {A} / defender {B} (stake {Stake}, refund after {Refund})",
            matchId, challengerAddress, defenderAddress, stakeSats, refundAfterUnixSeconds);
        return new WagerEscrowInfo(matchId, challengerAddress, defenderAddress, stakeSats, stakeSats * 2, refundAfterUnixSeconds);
    }

    private async Task<Covenants.WagerEscrowParams> RequireEscrowParamsAsync(string matchId, CancellationToken ct)
    {
        var json = await GetKvAsync($"escrow:{matchId}", ct)
                   ?? throw new InvalidOperationException($"No covenant escrow recorded for match {matchId}.");
        return System.Text.Json.JsonSerializer.Deserialize<Covenants.WagerEscrowParams>(json)!;
    }

    public async Task<Covenants.WagerEscrowParams?> GetWagerEscrowParamsAsync(string matchId, CancellationToken ct = default)
    {
        var json = await GetKvAsync($"escrow:{matchId}", ct);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<Covenants.WagerEscrowParams>(json);
    }

    /// <summary>
    /// A stake is a pure-BTC VTXO of the exact amount — NEVER an asset carrier.
    /// Now that item/hero/XP assets circulate, an asset VTXO could sit at the
    /// same sat value as the stake; sweeping it as a stake would spend the wrong
    /// coin (and drop its asset). Funded-checks and settle-selection both require
    /// no assets so only the real BTC stake qualifies.
    /// </summary>
    private static bool IsBtcStake(ArkVtxo v, long stakeSats)
        => (long)v.Amount == stakeSats && v.Assets is null or { Count: 0 };

    public async Task<bool> IsEscrowFundedAsync(string matchId, CancellationToken ct = default)
    {
        var parameters = await RequireEscrowParamsAsync(matchId, ct);
        var (challengerContract, defenderContract) = await BuildEscrowContractsAsync(parameters, ct);
        var challengerScript = challengerContract.GetArkAddress().ScriptPubKey.ToHex();
        var defenderScript = defenderContract.GetArkAddress().ScriptPubKey.ToHex();
        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { challengerScript, defenderScript });
        var challengerVtxos = await vtxoStorage.GetVtxos(scripts: [challengerScript], cancellationToken: ct);
        var defenderVtxos = await vtxoStorage.GetVtxos(scripts: [defenderScript], cancellationToken: ct);
        return challengerVtxos.Any(v => IsBtcStake(v, parameters.StakeSats))
               && defenderVtxos.Any(v => IsBtcStake(v, parameters.StakeSats));
    }

    public async Task<string> SettleWagerEscrowAsync(
        string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64,
        CancellationToken ct = default)
    {
        var parameters = await RequireEscrowParamsAsync(matchId, ct);
        var (challengerContract, defenderContract) = await BuildEscrowContractsAsync(parameters, ct);
        var challengerScript = challengerContract.GetArkAddress().ScriptPubKey.ToHex();
        var defenderScript = defenderContract.GetArkAddress().ScriptPubKey.ToHex();

        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { challengerScript, defenderScript });
        var challengerStake = (await vtxoStorage.GetVtxos(scripts: [challengerScript], cancellationToken: ct))
            .FirstOrDefault(v => IsBtcStake(v, parameters.StakeSats));
        var defenderStake = (await vtxoStorage.GetVtxos(scripts: [defenderScript], cancellationToken: ct))
            .FirstOrDefault(v => IsBtcStake(v, parameters.StakeSats));
        if (challengerStake is null || defenderStake is null)
            throw new InvalidOperationException($"Escrow for {matchId} is not fully funded.");

        var branch = challengerWon ? "settleToChallenger" : "settleToDefender";
        var winnerAddress = challengerWon ? parameters.ChallengerAddress : parameters.DefenderAddress;
        var pot = parameters.StakeSats * 2;

        // Witness: [outputIndex, otherInputIndex, serverSeed, oracleSig] — sig on top.
        Covenants.CovenantSpender.CovenantInput[] inputs =
        [
            new(challengerContract, branch,
                [Covenants.ArkadeCovenants.EncodeIndex(0), Covenants.ArkadeCovenants.EncodeIndex(1), serverSeed, oracleSignature64],
                challengerStake),
            new(defenderContract, branch,
                [Covenants.ArkadeCovenants.EncodeIndex(0), Covenants.ArkadeCovenants.EncodeIndex(0), serverSeed, oracleSignature64],
                defenderStake),
        ];

        var response = await Covenants.CovenantSpender.SpendManyCoreAsync(
            transport,
            safetyService,
            walletProvider,
            intentStorage,
            _treasuryWalletId!,
            new Uri(options.EmulatorUri),
            inputs,
            [new TxOut(Money.Satoshis(pot), ArkAddress.Parse(winnerAddress))],
            ct: ct);

        logger.LogInformation("Escrow for {MatchId} settled to {Winner} via covenant ({Branch})",
            matchId, winnerAddress, branch);
        return string.IsNullOrEmpty(response.SignedArkTx) ? "settled" : "covenant-settled";
    }

    // ── Covenant breeding escrows ──────────────────────────────────────

    private async Task<(Covenants.ArkadeArtifactContract Contract, global::NArk.Core.ArkServerInfo ServerInfo)>
        BuildBreedContractAsync(Covenants.BreedEscrowParams parameters, CancellationToken ct)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorKey = await RequireEmulatorSignerAsync(ct);
        var contract = Covenants.BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorKey);
        return (contract, serverInfo);
    }

    public async Task<BreedEscrowInfo> CreateBreedEscrowAsync(
        string breedingId, string playerId, string parentAAssetId, string parentBAssetId,
        long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await EnsureTreasuryAsync(ct);
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var species = await EnsureSpeciesAssetAsync(ct);
        var playerAddress = await GetPlayerAddressAsync(playerId, ct);
        var isMain = serverInfo.Network == Network.Main;
        var escrowSats = feeSats + 2 * serverInfo.Dust.Satoshi; // fee + two parent carriers

        var parameters = new Covenants.BreedEscrowParams(
            playerAddress, parentAAssetId, parentBAssetId, species,
            _treasuryAddress!, feeSats, escrowSats, oraclePubKeyHex, breedingId, refundAfterUnixSeconds);
        await SetKvAsync($"breed-escrow:{breedingId}", System.Text.Json.JsonSerializer.Serialize(parameters), ct);

        var (contract, _) = await BuildBreedContractAsync(parameters, ct);
        var address = contract.GetArkAddress().ToString(isMain);
        logger.LogInformation("Breed escrow for {BreedingId}: {Address} (fee {Fee}, refund after {Refund})",
            breedingId, address, feeSats, refundAfterUnixSeconds);
        return new BreedEscrowInfo(breedingId, address, feeSats, refundAfterUnixSeconds);
    }

    private async Task<Covenants.BreedEscrowParams> RequireBreedParamsAsync(string breedingId, CancellationToken ct)
    {
        var json = await GetKvAsync($"breed-escrow:{breedingId}", ct)
                   ?? throw new InvalidOperationException($"No breed escrow recorded for {breedingId}.");
        return System.Text.Json.JsonSerializer.Deserialize<Covenants.BreedEscrowParams>(json)!;
    }

    public async Task<Covenants.BreedEscrowParams?> GetBreedEscrowParamsAsync(string breedingId, CancellationToken ct = default)
    {
        var json = await GetKvAsync($"breed-escrow:{breedingId}", ct);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<Covenants.BreedEscrowParams>(json);
    }

    private async Task<IReadOnlyList<ArkVtxo>> BreedEscrowVtxosAsync(Covenants.BreedEscrowParams parameters, CancellationToken ct)
    {
        var (contract, _) = await BuildBreedContractAsync(parameters, ct);
        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
        return (await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct)).DistinctBy(v => v.OutPoint).ToList();
    }

    public async Task<bool> IsBreedEscrowFundedAsync(string breedingId, CancellationToken ct = default)
    {
        var parameters = await RequireBreedParamsAsync(breedingId, ct);
        var vtxos = await BreedEscrowVtxosAsync(parameters, ct);
        bool Holds(string assetId) => vtxos.Any(v => v.Assets?.Any(a => a.AssetId == assetId) == true);
        var totalSats = vtxos.Aggregate(0L, (s, v) => s + (long)v.Amount);
        return Holds(parameters.ParentAId) && Holds(parameters.ParentBId) && totalSats >= parameters.FeeSats;
    }

    public async Task<HeroMintResult> ExecuteBreedCovenantAsync(
        string breedingId, HeroMintData childData, byte[] oracleSignature64, CancellationToken ct = default)
    {
        var parameters = await RequireBreedParamsAsync(breedingId, ct);
        var (contract, serverInfo) = await BuildBreedContractAsync(parameters, ct);
        var vtxos = await BreedEscrowVtxosAsync(parameters, ct);

        // Order inputs so parentA carrier is vin 0, parentB vin 1 (the builder
        // preserves input order — ShuffleInputs=false), then any fee-only VTXOs.
        int Idx(string assetId) => vtxos.ToList().FindIndex(v => v.Assets?.Any(a => a.AssetId == assetId) == true);
        var iaSrc = Idx(parameters.ParentAId);
        var ibSrc = Idx(parameters.ParentBId);
        if (iaSrc < 0 || ibSrc < 0)
            throw new InvalidOperationException($"Breed escrow {breedingId} is missing a parent.");
        var ordered = new List<ArkVtxo> { vtxos[iaSrc], vtxos[ibSrc] };
        ordered.AddRange(vtxos.Where((_, i) => i != iaSrc && i != ibSrc));

        var species = global::NArk.Core.Assets.AssetId.FromString(parameters.SpeciesId);
        var parentA = global::NArk.Core.Assets.AssetId.FromString(parameters.ParentAId);
        var parentB = global::NArk.Core.Assets.AssetId.FromString(parameters.ParentBId);
        var playerScript = ArkAddress.Parse(parameters.PlayerAddress).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(parameters.TreasuryFeeAddress).ScriptPubKey;

        // Packet: parents retained to the player (vins 0/1), child issued under
        // the species with the oracle-attested metadata (group 2).
        var childMeta = Covenants.BreedEscrowContracts.ChildMetadata(
            childData.GenomeHex, childData.Generation, childData.ParentAId ?? "", childData.ParentBId ?? "",
            childData.ServerSeedHex ?? "", childData.PlayerNonce ?? "");
        var packet = global::NArk.Core.Assets.Packet.Create(
        [
            global::NArk.Core.Assets.AssetGroup.Create(parentA, null,
                [global::NArk.Core.Assets.AssetInput.Create(0, 1)], [global::NArk.Core.Assets.AssetOutput.Create(0, 1)], []),
            global::NArk.Core.Assets.AssetGroup.Create(parentB, null,
                [global::NArk.Core.Assets.AssetInput.Create(1, 1)], [global::NArk.Core.Assets.AssetOutput.Create(0, 1)], []),
            global::NArk.Core.Assets.AssetGroup.Create(null, global::NArk.Core.Assets.AssetRef.FromId(species),
                [], [global::NArk.Core.Assets.AssetOutput.Create(0, 1)], childMeta),
        ]);

        var total = ordered.Aggregate(0L, (s, v) => s + (long)v.Amount);
        var inputs = ordered.Select(v => new Covenants.CovenantSpender.CovenantInput(
            contract, "breed",
            Covenants.ArkadeCovenants.BreedWitness(oracleSignature64, 2, feeOutputIndex: 1, 0, 1), v)).ToList();

        var response = await Covenants.CovenantSpender.SpendManyCoreAsync(
            transport, safetyService, walletProvider, intentStorage,
            _treasuryWalletId!, new Uri(options.EmulatorUri), inputs,
            [
                new TxOut(Money.Satoshis(total - parameters.FeeSats), playerScript), // change + assets → player
                new TxOut(Money.Satoshis(parameters.FeeSats), treasuryScript),        // fee → treasury (distinct)
            ],
            extraPackets: [packet], ct: ct);

        // Child asset id = (breed txid, group index 2), NArk display form.
        var breedTxId = NBitcoin.PSBT.Parse(response.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        var childAssetId = global::NArk.Core.Assets.AssetId.Create(breedTxId, 2).ToString();
        logger.LogInformation("Breed {BreedingId} executed via covenant → child {Child} to player", breedingId, childAssetId);
        return new HeroMintResult(childAssetId, breedTxId);
    }

    // ── Covenant item offers (resting, buyer-fulfilled) ────────────────

    private async Task<(Covenants.ArkadeArtifactContract Contract, global::NArk.Core.ArkServerInfo ServerInfo)>
        BuildOfferContractAsync(Covenants.OfferParams parameters, CancellationToken ct)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorKey = await RequireEmulatorSignerAsync(ct);
        var contract = Covenants.OfferContracts.Build(parameters, serverInfo.SignerKey, emulatorKey);
        return (contract, serverInfo);
    }

    public async Task<OfferInfo> CreateOfferAsync(
        string offerId, string sellerPlayerId, string itemId, long askSats,
        long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        var assetId = await GetKvAsync($"itemAsset:{itemId}", ct)
            ?? throw new InvalidOperationException($"Item '{itemId}' has never been issued — nothing to sell.");
        return await CreateOfferForAssetAsync(offerId, sellerPlayerId, assetId, askSats, refundAfterUnixSeconds, ct);
    }

    public Task<OfferInfo> CreateHeroOfferAsync(
        string offerId, string sellerPlayerId, string heroAssetId, long askSats,
        long refundAfterUnixSeconds, CancellationToken ct = default)
        => CreateOfferForAssetAsync(offerId, sellerPlayerId, heroAssetId, askSats, refundAfterUnixSeconds, ct);

    /// <summary>
    /// The asset-agnostic core: rest an offer for a specific asset id (a fungible
    /// item's shared asset, or a hero's own unique asset). The offer covenant only
    /// ever sees an asset id, so items and heroes share this path.
    /// </summary>
    private async Task<OfferInfo> CreateOfferForAssetAsync(
        string offerId, string sellerPlayerId, string assetId, long askSats,
        long refundAfterUnixSeconds, CancellationToken ct)
    {
        if (askSats <= 0) throw new InvalidOperationException("The ask must be positive.");
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var sellerAddress = await GetPlayerAddressAsync(sellerPlayerId, ct);
        // The carrier dust the seller deposits with the asset is exactly what the
        // reclaim leaf pays back, so it must match serverInfo.Dust (what
        // SendAssetAsync deposits).
        var offerValue = serverInfo.Dust.Satoshi;

        var parameters = new Covenants.OfferParams(
            sellerAddress, assetId, askSats, offerValue, offerId, refundAfterUnixSeconds);
        await SetKvAsync($"offer:{offerId}", System.Text.Json.JsonSerializer.Serialize(parameters), ct);

        var (contract, _) = await BuildOfferContractAsync(parameters, ct);
        var address = contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
        logger.LogInformation("Offer {OfferId}: {Address} (asset {Asset}, ask {Ask}, refund after {Refund})",
            offerId, address, assetId, askSats, refundAfterUnixSeconds);
        return new OfferInfo(offerId, address, assetId, askSats, offerValue, refundAfterUnixSeconds);
    }

    public async Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default)
    {
        var json = await GetKvAsync($"offer:{offerId}", ct);
        return json is null ? null : System.Text.Json.JsonSerializer.Deserialize<Covenants.OfferParams>(json);
    }

    public async Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default)
    {
        var parameters = await GetOfferParamsAsync(offerId, ct);
        if (parameters is null) return false;
        var (contract, _) = await BuildOfferContractAsync(parameters, ct);
        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
        var vtxos = (await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct)).DistinctBy(v => v.OutPoint).ToList();
        return vtxos.Any(v => v.Assets?.Any(a => a.AssetId == parameters.ItemAssetId) == true);
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
