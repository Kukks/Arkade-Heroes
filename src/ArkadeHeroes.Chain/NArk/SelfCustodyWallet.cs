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
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Safety.AsyncKeyedLock;
using NArk.Storage.EfCore.Hosting;
using NArk.Storage.EfCore.Storage;
using NBitcoin;

namespace ArkadeHeroes.Chain.NArk;

public class SelfCustodyWalletOptions
{
    public string ArkUri { get; set; } = "http://localhost:7070";

    /// <summary>SQLite file for the wallet's local storage (VTXOs, contracts, keys).</summary>
    public required string DbPath { get; set; }

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

    public static async Task<SelfCustodyWallet> CreateAsync(SelfCustodyWalletOptions options, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
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
        services.AddSingleton<IIntentScheduler, SimpleIntentScheduler>();
        services.AddSingleton<ISafetyService, AsyncSafetyService>();
        services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
        services.AddSingleton<IAssetManager, AssetManager>();

        var provider = services.BuildServiceProvider();
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

    /// <summary>Pays sats to an address (fee invoices, stakes) — signed locally.</summary>
    public async Task<string> SendAsync(string toAddress, long amountSats, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        var txId = await Spending.Spend(_walletId,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ArkAddress.Parse(toAddress)),
        ], cancellationToken: ct);
        return txId.ToString();
    }

    /// <summary>Sends asset units (hero transfer, item trade) — signed locally.</summary>
    public async Task<string> SendAssetAsync(string toAddress, string assetId, ulong amount, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        var serverInfo = await Transport.GetServerInfoAsync(ct);
        var txId = await Spending.Spend(_walletId,
        [
            new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, ArkAddress.Parse(toAddress))
            {
                Assets = [new ArkTxOutAsset(assetId, amount)],
            },
        ], cancellationToken: ct);
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
