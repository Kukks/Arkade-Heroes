using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
using NArk.Abstractions.Blockchain;
using NBitcoin;

namespace ArkadeHeroes.Chain.NArk;

public class SelfCustodyWalletOptions
{
    public string ArkUri { get; set; } = "http://localhost:7070";

    /// <summary>SQLite file for the wallet's local storage (VTXOs, contracts, keys).</summary>
    public required string DbPath { get; set; }

    /// <summary>Esplora REST API, and with it the chain time auto-renewal needs. Null — every existing
    /// caller's default — means no chain source and so no renewal, exactly as before.</summary>
    public string? EsploraUri { get; set; }

    /// <summary>BIP-39 mnemonic; generated fresh when null. NEVER leaves this process.</summary>
    public string? Mnemonic { get; set; }

    /// <summary>
    /// Opt-in passphrase to encrypt the wallet's mnemonic at rest (AES-256-GCM,
    /// see <see cref="WalletSecretCipher"/>). When null/empty the mnemonic is
    /// stored in cleartext (today's behaviour, used by the non-interactive E2E
    /// suite). When set, the wallet DB holds only ciphertext and the SAME
    /// passphrase is required to reopen it.
    /// </summary>
    public string? Passphrase { get; set; }
}

/// <summary>
/// A self-custody Arkade wallet for players: keys are generated and stored
/// locally, spends are signed locally, and the game server only ever learns
/// the receive address. Used by the console client and the E2E suite — this
/// is the client half of the non-custodial mandate.
///
/// Composition mirrors the SDK sample wallet (NArk core services + EF Core
/// SQLite storage) in an isolated service provider.
/// </summary>
public sealed class SelfCustodyWallet : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _walletId;

    public string Mnemonic { get; }
    public string Address { get; }

    /// <summary>
    /// The wallet's stable x-only LOGIN pubkey (hex): a keypair derived from the
    /// mnemonic on a fixed path, separate from the ark spending keys. It survives
    /// a restore (same words → same key) and is the "sign in with your wallet"
    /// identity a player registers so they can resume a server session later.
    /// </summary>
    public string LoginPubKeyHex { get; }

    private readonly global::NBitcoin.Secp256k1.ECPrivKey _loginKey;

    /// <summary>The wallet's NArk wallet id (informational; keys never leave this process).</summary>
    public string WalletId => _walletId;

    /// <summary>
    /// Advanced escape hatch into the wallet's isolated NArk service graph
    /// (transport, storage, tx builder deps) — used by covenant tooling that
    /// composes raw transactions on top of this wallet.
    /// </summary>
    public T GetService<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>The wallet's isolated NArk service graph — lets covenant flows run against
    /// this wallet or, via the same entry points, against any other NArk service container
    /// (e.g. a browser's Blazor DI).</summary>
    public IServiceProvider Services => _services;

    private SelfCustodyWallet(ServiceProvider services, string walletId, string mnemonic, string address)
    {
        _services = services;
        _walletId = walletId;
        Mnemonic = mnemonic;
        Address = address;

        // Derive a dedicated login keypair from the mnemonic on a fixed hardened
        // path (distinct from ark spending derivation), so it is deterministic
        // across restores yet never doubles as a spending key.
        var loginPriv = new Mnemonic(mnemonic).DeriveExtKey()
            .Derive(new KeyPath("83696968'/0'/0'")).PrivateKey.ToBytes();
        _loginKey = global::NBitcoin.Secp256k1.ECPrivKey.Create(loginPriv);
        Span<byte> pub = stackalloc byte[32];
        _loginKey.CreateXOnlyPubKey().WriteToSpan(pub);
        LoginPubKeyHex = Convert.ToHexString(pub).ToLowerInvariant();
    }

    /// <summary>
    /// Signs a 32-byte login-challenge digest with the stable login key (BIP340),
    /// returning the x-only pubkey + signature hex — proves control of the login
    /// identity without revealing the key. The caller computes the digest (the
    /// shared <c>LoginChallenge.Digest</c>) so the formula lives in one place.
    /// </summary>
    public (string PubKeyHex, string SignatureHex) SignLoginDigest(byte[] digest32)
    {
        var sig = _loginKey.SignBIP340(digest32);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        return (LoginPubKeyHex, Convert.ToHexString(sigBytes).ToLowerInvariant());
    }

    /// <summary>
    /// Validates a BIP39 recovery phrase — the word count, that every word is in
    /// the English wordlist, and the checksum — returning a human-readable reason
    /// when it's wrong (null when valid). Lets a restore reject a typo'd phrase with
    /// a clear message up front instead of failing deep in wallet creation. Expects
    /// whitespace-normalized, lower-cased input.
    /// </summary>
    public static string? ValidateMnemonic(string mnemonic)
    {
        var words = mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
            return $"a recovery phrase is 12 or 24 words — this has {words.Length}";
        try
        {
            if (!new Mnemonic(mnemonic, Wordlist.English).IsValidChecksum)
                return "every word is valid but the checksum isn't — check the word order or for a single typo";
            return null;
        }
        catch (FormatException fx)
        {
            return $"not a valid BIP39 recovery phrase — {fx.Message}";
        }
    }

    public static IServiceCollection ConfigureServices(IServiceCollection services, SelfCustodyWalletOptions options)
    {
        services.AddLogging();
        services.AddDbContextFactory<GameArkDbContext>(builder =>
            builder.UseSqlite($"Data Source={options.DbPath}"));
        services.AddArkEfCoreStorage<GameArkDbContext>();
        // Opt-in at-rest encryption: keep the concrete EfCoreWalletStorage that
        // AddArkEfCoreStorage registered, but swap the IWalletStorage the SDK
        // resolves for an encrypting decorator over it (transparent to NArk).
        if (!string.IsNullOrEmpty(options.Passphrase))
        {
            services.RemoveAll<IWalletStorage>();
            services.AddSingleton<IWalletStorage>(sp =>
                new EncryptingWalletStorage(sp.GetRequiredService<EfCoreWalletStorage>(), options.Passphrase));
        }
        services.AddArkCoreServices();
        services.AddArkNetwork(new ArkNetworkConfig(options.ArkUri));
        if (options.EsploraUri is { Length: > 0 } esploraUri)
        {
            services.AddSingleton<IBitcoinBlockchain>(_ =>
                new EsploraBlockchain(new Uri(esploraUri.TrimEnd('/') + "/")));
        }
        services.AddArkadeRenewalScheduling();
        services.AddSingleton<ISafetyService, AsyncSafetyService>();
        services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
        services.AddSingleton<IAssetManager, AssetManager>();
        return services;
    }

    public static async Task<SelfCustodyWallet> CreateAsync(SelfCustodyWalletOptions options, CancellationToken ct = default)
    {
        var provider = ConfigureServices(new ServiceCollection(), options).BuildServiceProvider();
        try
        {
            await using (var db = await provider.GetRequiredService<IDbContextFactory<GameArkDbContext>>()
                             .CreateDbContextAsync(ct))
                await db.Database.EnsureCreatedAsync(ct);

            var transport = provider.GetRequiredService<global::NArk.Core.Transport.IClientTransport>();
            var serverInfo = await transport.GetServerInfoAsync(ct);

            var walletStorage = provider.GetRequiredService<IWalletStorage>();
            var existing = (await walletStorage.LoadAllWallets(ct)).FirstOrDefault();

            string walletId;
            string mnemonic;
            if (existing is not null && !string.IsNullOrEmpty(existing.Secret))
            {
                // With a passphrase the storage decorator has already decrypted
                // the secret; without one, a still-encrypted secret can't be used
                // as a mnemonic — fail clearly instead of deriving a junk wallet.
                if (WalletSecretCipher.IsEncrypted(existing.Secret))
                    throw new InvalidOperationException(
                        "This wallet is encrypted — provide its passphrase (SelfCustodyWalletOptions.Passphrase) to open it.");
                walletId = existing.Id;
                mnemonic = existing.Secret!;
            }
            else
            {
                mnemonic = options.Mnemonic ?? new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
                var wallet = await WalletFactory.CreateWallet(mnemonic, null, serverInfo, ct);
                await walletStorage.SaveWallet(wallet, ct);
                walletId = wallet.Id;
            }

            // The wallet's receive contract — one address the game knows us by,
            // persisted in the wallet DB so restarts keep the same identity.
            var dbFactory = provider.GetRequiredService<IDbContextFactory<GameArkDbContext>>();
            string? address;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                address = (await db.ChainKv.FindAsync(["primaryAddress"], ct))?.Value;
            if (address is null)
            {
                var contractService = provider.GetRequiredService<IContractService>();
                var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive, cancellationToken: ct);
                address = contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                db.ChainKv.Add(new GameChainKv { Key = "primaryAddress", Value = address });
                await db.SaveChangesAsync(ct);
            }

            // Start VTXO synchronization so receives/spends are visible.
            var vtxoSync = provider.GetRequiredService<VtxoSynchronizationService>();
            await vtxoSync.StartAsync(ct);

            return new SelfCustodyWallet(provider, walletId, mnemonic, address);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    private IVtxoStorage VtxoStorage => _services.GetRequiredService<IVtxoStorage>();
    private VtxoSynchronizationService VtxoSync => _services.GetRequiredService<VtxoSynchronizationService>();
    private SpendingService Spending => _services.GetRequiredService<SpendingService>();
    private global::NArk.Core.Transport.IClientTransport Transport =>
        _services.GetRequiredService<global::NArk.Core.Transport.IClientTransport>();

    /// <summary>Forces a sync of this wallet's scripts against the operator's indexer.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        var contractStorage = _services.GetRequiredService<IContractStorage>();
        var contracts = await contractStorage.GetContracts(walletIds: [_walletId], cancellationToken: ct);
        foreach (var contract in contracts)
            await VtxoSync.PollScriptsForVtxos(new HashSet<string> { contract.Script });
    }

    public async Task<long> GetBalanceSatsAsync(CancellationToken ct = default)
    {
        await SyncAsync(ct);
        var vtxos = await VtxoStorage.GetVtxos(walletIds: [_walletId], cancellationToken: ct);
        return vtxos.Aggregate(0L, (sum, v) => sum + (long)v.Amount);
    }

    public async Task<IReadOnlyList<(string AssetId, ulong Amount)>> GetAssetsAsync(CancellationToken ct = default)
    {
        await SyncAsync(ct);
        var vtxos = await VtxoStorage.GetVtxos(walletIds: [_walletId], cancellationToken: ct);
        return vtxos
            .Where(v => v.Assets is { Count: > 0 })
            .SelectMany(v => v.Assets!)
            .GroupBy(a => a.AssetId)
            .Select(g => (g.Key, g.Aggregate(0UL, (sum, a) => sum + a.Amount)))
            .ToList();
    }

    /// <summary>
    /// Selects the wallet's SPENDABLE coins as explicit inputs, EXCLUDING
    /// recoverable ones (swept or past expiry). NArk's automatic selection
    /// (<c>Spend(outputs)</c>) offers recoverable coins to selection, and a spend
    /// that lands on one is rejected by arkd with <c>VTXO_RECOVERABLE</c>; the
    /// explicit-inputs overload computes change, so a filtered set spends cleanly.
    /// Uses the SDK's fallback chain time (now, height 0), which catches both swept
    /// and time-expired coins. The choice itself is <see cref="SelectFrom"/>.
    /// </summary>
    private async Task<ArkCoin[]> SelectSpendableCoinsAsync(ArkTxOut[] outputs, CancellationToken ct)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var spendable = (await Spending.GetAvailableCoins(_walletId, ct))
            .Where(c => c.CanSpendOffchain(now))
            .ToList();
        var dust = (await Transport.GetServerInfoAsync(ct)).Dust.Satoshi;
        return SelectFrom(spendable, outputs, dust);
    }

    /// <summary>
    /// Which of the given coins to spend: the carriers for each output asset, then the largest
    /// pure-BTC coins until the change clears dust, falling back to a carrier when no pure-BTC coin
    /// can cover the sats, and refusing outright when the spend would leave asset change with
    /// nowhere of the wallet's own to land.
    ///
    /// <para>Deliberately a DUPLICATE of the browser wallet's selection in
    /// <c>ArkadeHeroes.Web.Wallet.GameWallet.SelectSpendableCoinsAsync</c>, not a shared helper.
    /// The two differ in what they raise: this one throws <see cref="InvalidOperationException"/>
    /// with operator-facing detail for the console and the E2E suite, while the browser throws a
    /// player-facing exception and additionally separates the settlement-lag case from a real
    /// shortfall using the unfiltered coin set this side never reads. Folding them together would
    /// mean threading message factories through the browser path that was proven live against arkd,
    /// to save a function this size — a worse trade than keeping two copies honest. Keep them in
    /// step by hand: a change to one belongs in the other.</para>
    ///
    /// <para>Pure and static so the rules below can be tested directly — they are the rules that
    /// decide where a player's heroes end up.</para>
    /// </summary>
    internal static ArkCoin[] SelectFrom(IReadOnlyCollection<ArkCoin> spendable, ArkTxOut[] outputs, long dust)
    {
        var selected = new HashSet<ArkCoin>();

        static ulong AssetHeld(ArkCoin c, string assetId) =>
            c.Assets?.Where(a => a.AssetId == assetId).Aggregate(0UL, (s, a) => s + a.Amount) ?? 0;

        // 1. Cover each output asset with the coins that carry it (most first).
        var neededAssets = outputs
            .Where(o => o.Assets is { Count: > 0 })
            .SelectMany(o => o.Assets!)
            .GroupBy(a => a.AssetId)
            .ToDictionary(g => g.Key, g => g.Aggregate(0UL, (s, a) => s + a.Amount));
        foreach (var (assetId, needed) in neededAssets)
        {
            ulong held = 0;
            foreach (var coin in spendable.Where(c => AssetHeld(c, assetId) > 0)
                         .OrderByDescending(c => AssetHeld(c, assetId)))
            {
                if (held >= needed) break;
                if (selected.Add(coin)) held += AssetHeld(coin, assetId);
            }
            if (held < needed)
                throw new InvalidOperationException(
                    $"Wallet has no spendable coins for {needed} unit(s) of asset {assetId} (recoverable coins excluded).");
        }

        // 2. Cover the sats total with the largest PURE-BTC coins (so a sats send
        //    doesn't drag in unrelated asset VTXOs).
        long required = outputs.Sum(o => o.Value.Satoshi);
        var btc = spendable
            .Where(c => !selected.Contains(c) && c.Assets is null or { Count: 0 })
            .OrderByDescending(c => c.TxOut.Value.Satoshi).ToList();
        // Take coins until the change this leaves is a real VTXO (at or above dust), not merely
        // until the outputs are covered: sub-dust change is what misplaces asset change (step 4).
        // This replaces an unconditional "grab one more coin for headroom", which took a coin the
        // spend did not need and left the SDK's one-MINUTE per-input lock — never released on
        // failure — sitting on it. That spare is exactly the coin a retry reaches for.
        var i = 0;
        while (selected.Sum(c => c.TxOut.Value.Satoshi) < required + dust && i < btc.Count)
            selected.Add(btc[i++]);

        // 3. A settlement round can consolidate the whole wallet into ONE VTXO that carries the
        //    sats AND every hero the player owns. After that there is no pure-BTC coin left, so no
        //    fee could ever be paid again — every spend fails with "lacks spendable sats" while the
        //    balance still shows the full amount. Fall back to spending a carrier: the SDK's asset
        //    packet assigns every unspent input asset to the change output, so the heroes ride home
        //    to the wallet's own change address. Only take a carrier that leaves change at or above
        //    dust — below dust the change output moves to vout 0 while asset change still points at
        //    the LAST output, which would hand the heroes to the recipient.
        if (selected.Sum(c => c.TxOut.Value.Satoshi) < required)
        {
            foreach (var carrier in spendable
                         .Where(c => !selected.Contains(c) && c.Assets is { Count: > 0 })
                         .OrderByDescending(c => c.TxOut.Value.Satoshi))
            {
                if (selected.Sum(c => c.TxOut.Value.Satoshi) + carrier.TxOut.Value.Satoshi < required + dust) continue;
                selected.Add(carrier);
                break;
            }
        }

        if (selected.Sum(c => c.TxOut.Value.Satoshi) < required)
            throw new InvalidOperationException(
                $"Wallet lacks {required} spendable sats (recoverable coins excluded).");

        // 4. The last line of defence for whatever the inputs are still carrying. Wherever the
        //    selected coins hold asset units the outputs do NOT consume, the SDK puts that leftover
        //    on the change output — but only when there IS one. Change below dust is moved to an
        //    OP_RETURN at vout 0 while the asset packet still points at the LAST output, and change
        //    of exactly zero produces no change output at all; either way the leftover hero lands
        //    on the recipient. Step 3 already holds this line for the case it handles, but it is
        //    not the only way a carrier gets selected — step 1 picks one too, and that path can
        //    land here with a coin that holds two heroes and barely more than dust.
        if (selected.Sum(c => c.TxOut.Value.Satoshi) - required < dust && LeavesAssetsBehind(selected, outputs))
            throw new InvalidOperationException(
                "Refusing to spend: the selected coins leave asset change, but the sats left over are " +
                "below dust, so there is no change output to carry it and it would land on the recipient.");

        return [.. selected];
    }

    /// <summary>
    /// True when the chosen inputs hold asset units the outputs don't spend — i.e. there is asset
    /// change, and it therefore needs somewhere of the wallet's own to land. Mirrors the SDK's own
    /// <c>HasAssetChange</c>, which is what decides that a change output is required.
    /// </summary>
    private static bool LeavesAssetsBehind(IEnumerable<ArkCoin> inputs, ArkTxOut[] outputs)
    {
        var held = new Dictionary<string, ulong>();
        foreach (var coin in inputs)
            foreach (var asset in coin.Assets ?? [])
                held[asset.AssetId] = held.GetValueOrDefault(asset.AssetId) + asset.Amount;
        if (held.Count == 0) return false;

        var sent = new Dictionary<string, ulong>();
        foreach (var output in outputs)
            foreach (var asset in output.Assets ?? [])
                sent[asset.AssetId] = sent.GetValueOrDefault(asset.AssetId) + asset.Amount;

        return held.Any(kv => kv.Value > sent.GetValueOrDefault(kv.Key));
    }

    /// <summary>Pays sats to an address (fee invoices, stakes) — signed locally.</summary>
    public async Task<string> SendAsync(string toAddress, long amountSats, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        ArkTxOut[] outputs =
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ArkAddress.Parse(toAddress)),
        ];
        var txId = await Spending.Spend(_walletId, await SelectSpendableCoinsAsync(outputs, ct), outputs, ct);
        return txId.ToString();
    }

    /// <summary>Sends asset units (hero transfer, item trade) — signed locally.</summary>
    public async Task<string> SendAssetAsync(string toAddress, string assetId, ulong amount, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        var serverInfo = await Transport.GetServerInfoAsync(ct);
        ArkTxOut[] outputs =
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(toAddress))
            {
                Assets = [new ArkTxOutAsset(assetId, amount)],
            },
        ];
        var txId = await Spending.Spend(_walletId, await SelectSpendableCoinsAsync(outputs, ct), outputs, ct);
        return txId.ToString();
    }

    /// <summary>Waits until the wallet holds the given asset (e.g. a freshly minted hero).</summary>
    public async Task WaitForAssetAsync(string assetId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await GetAssetsAsync(ct)).Any(a => a.AssetId == assetId && a.Amount > 0))
                return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Asset {assetId} did not arrive in the wallet within {timeout}.");
    }

    /// <summary>Waits until the wallet's spendable balance reaches the given amount.</summary>
    public async Task WaitForBalanceAsync(long minSats, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await GetBalanceSatsAsync(ct) >= minSats) return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Balance did not reach {minSats} sats within {timeout}.");
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
