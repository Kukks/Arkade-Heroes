using ArkadeHeroes.Shared;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Core.Wallet;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// The player's non-custodial wallet, entirely in the browser. A focused, game-flavoured
/// facade over the NArk SDK services (mirrors the essentials of the sample's ArkWalletService):
/// generate/import a mnemonic, derive the receive address, read balance + VTXOs. The keys
/// (the BIP-39 mnemonic) live only in the tab's SQLite store (Bit.Besql → browser storage) —
/// they never reach the game server.
/// </summary>
public class GameWallet(
    IWalletStorage walletStorage,
    IClientTransport transport,
    ISpendingService spendingService,
    IVtxoStorage vtxoStorage,
    IContractService contractService)
{
    /// <summary>All wallets held in this browser (the game uses at most one).</summary>
    public async Task<IReadOnlySet<ArkWalletInfo>> GetWalletsAsync()
        => await walletStorage.LoadAllWallets();

    /// <summary>The single active wallet, or null if the player hasn't created one yet.</summary>
    public async Task<ArkWalletInfo?> GetActiveWalletAsync()
        => (await walletStorage.LoadAllWallets()).FirstOrDefault();

    public async Task<bool> HasWalletAsync()
        => (await walletStorage.LoadAllWallets()).Count > 0;

    /// <summary>
    /// Create a brand-new wallet from a fresh 12-word BIP-39 mnemonic (an HD wallet — the
    /// non-"nsec" secret routes WalletFactory to HD derivation). Returns the wallet plus the
    /// mnemonic so the UI can show it once for the player to back up.
    /// </summary>
    public async Task<(ArkWalletInfo Wallet, string Mnemonic)> CreateAsync(CancellationToken ct = default)
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var wallet = await ImportAsync(mnemonic, ct);
        return (wallet, mnemonic);
    }

    /// <summary>
    /// Import (or restore the identity of) a wallet from an existing 12/24-word mnemonic.
    /// Idempotent — the same words re-derive the same wallet id and receive address, so this
    /// recovers a wallet's on-chain heroes and funds as the background sync discovers its VTXOs.
    /// Throws <see cref="GameWalletException"/> if the phrase is not a valid BIP-39 mnemonic.
    /// </summary>
    public async Task<ArkWalletInfo> ImportAsync(string mnemonic, CancellationToken ct = default)
    {
        var normalized = NormalizeMnemonic(mnemonic);
        // Validate up front so a typo fails here with a clear message rather than deep in the SDK.
        try
        {
            _ = new Mnemonic(normalized, Wordlist.English);
        }
        catch (Exception ex)
        {
            throw new GameWalletException("That doesn't look like a valid recovery phrase. " +
                "Check the words and spacing (12 or 24 lowercase words).", ex);
        }

        var serverInfo = await transport.GetServerInfoAsync();
        var wallet = await WalletFactory.CreateWallet(normalized, null, serverInfo, ct);
        await walletStorage.SaveWallet(wallet);
        return wallet;
    }

    /// <summary>
    /// The player's Arkade receive address — where heroes and funds are sent. Derives (and
    /// persists) the wallet's receive contract so incoming VTXOs to it become discoverable.
    /// </summary>
    public async Task<string> GetReceiveAddressAsync(string walletId, CancellationToken ct = default)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
        return contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
    }

    /// <summary>Spendable balance in sats (sum of unlocked, in-bounds VTXOs). 0 on any sync error.</summary>
    public async Task<long> GetBalanceAsync(string walletId)
    {
        try
        {
            var coins = await spendingService.GetAvailableCoins(walletId);
            return coins.Sum(c => c.Amount.Satoshi);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The wallet's VTXOs (its coins and hero/asset carriers), newest first by default.</summary>
    public async Task<IReadOnlyCollection<ArkVtxo>> GetVtxosAsync(string walletId, int skip = 0, int take = 50)
        => await vtxoStorage.GetVtxos(walletIds: [walletId], skip: skip, take: take);

    /// <summary>
    /// Send sats to another Arkade address — a real, non-custodial VTXO spend, built and signed
    /// in the browser. Returns the resulting Arkade tx id. Throws <see cref="GameWalletException"/>
    /// on a bad address, non-positive amount, or a spend failure (e.g. insufficient funds).
    /// </summary>
    public async Task<string> SendSatsAsync(string walletId, string destinationAddress, long amountSats)
    {
        if (amountSats <= 0) throw new GameWalletException("Enter an amount greater than zero.");
        var output = new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(amountSats), ParseAddress(destinationAddress));
        return await SpendAsync(walletId, [output]);
    }

    /// <summary>
    /// Send asset units (a hero or item) to an Arkade address — a plain non-custodial send used
    /// for direct transfers AND for depositing into a covenant escrow (breed/merge/stake): the
    /// escrow is just an opaque address, and the covenant enforcement lives server/emulator-side.
    /// Built + signed in the browser.
    /// </summary>
    public async Task<string> SendAssetAsync(string walletId, string destinationAddress, string assetId, ulong amount = 1)
    {
        var dest = ParseAddress(destinationAddress);
        var serverInfo = await transport.GetServerInfoAsync();
        var output = new ArkTxOut(ArkTxOutType.Vtxo, serverInfo.Dust, dest)
        {
            Assets = [new ArkTxOutAsset(assetId, amount)],
        };
        return await SpendAsync(walletId, [output]);
    }

    private static ArkAddress ParseAddress(string address)
    {
        try { return ArkAddress.Parse(address.Trim()); }
        catch (Exception ex) { throw new GameWalletException("That isn't a valid Arkade address.", ex); }
    }

    private async Task<string> SpendAsync(string walletId, ArkTxOut[] outputs)
    {
        try
        {
            var coins = await SelectSpendableCoinsAsync(walletId, outputs);
            var txId = await spendingService.Spend(walletId, coins, outputs);
            return txId.ToString();
        }
        catch (GameWalletException) { throw; }
        catch (Exception ex) { throw new GameWalletException($"Send failed: {ex.Message}", ex); }
    }

    // Explicit coin selection mirroring SelfCustodyWallet: exclude recoverable (swept/expired)
    // coins, cover each output asset with its carrier VTXOs, then the sats with the largest
    // pure-BTC coins plus one for headroom (fee + change).
    private async Task<ArkCoin[]> SelectSpendableCoinsAsync(string walletId, ArkTxOut[] outputs)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var spendable = (await spendingService.GetAvailableCoins(walletId))
            .Where(c => c.CanSpendOffchain(now))
            .ToList();
        var selected = new HashSet<ArkCoin>();

        static ulong AssetHeld(ArkCoin c, string assetId) =>
            c.Assets?.Where(a => a.AssetId == assetId).Aggregate(0UL, (s, a) => s + a.Amount) ?? 0;

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
                throw new GameWalletException($"You don't hold {needed} unit(s) of that asset yet.");
        }

        long required = outputs.Sum(o => o.Value.Satoshi);
        var btc = spendable
            .Where(c => !selected.Contains(c) && c.Assets is null or { Count: 0 })
            .OrderByDescending(c => c.TxOut.Value.Satoshi).ToList();
        var i = 0;
        while (selected.Sum(c => c.TxOut.Value.Satoshi) < required && i < btc.Count)
            selected.Add(btc[i++]);
        if (i < btc.Count) selected.Add(btc[i]);
        if (selected.Sum(c => c.TxOut.Value.Satoshi) < required)
            throw new GameWalletException("Not enough spendable sats (you need funds for the amount plus the network fee).");

        return [.. selected];
    }

    /// <summary>
    /// A snapshot of the wallet's currently-spendable pure-BTC coin outpoints. Take one before a
    /// send, then pass it to <see cref="WaitForSpendToSettleAsync"/> to know when that send has
    /// settled — essential when chaining sends that all draw on the same BTC coin (breed/merge deposits).
    /// </summary>
    public async Task<IReadOnlySet<string>> SpendableBtcOutpointsAsync(string walletId)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        return (await spendingService.GetAvailableCoins(walletId))
            .Where(c => c.CanSpendOffchain(now) && c.Assets is null or { Count: 0 })
            .Select(c => c.Outpoint.ToString())
            .ToHashSet();
    }

    /// <summary>
    /// After a send, wait until the wallet's spendable BTC set reflects it: a prior coin is GONE
    /// (the input was spent) AND a fresh coin has appeared (the change re-synced). Only then can the
    /// next send safely select — otherwise it may reuse the just-spent coin, which arkd has locked
    /// ("VTXO temporarily locked"), or find no change yet ("not enough spendable sats"). Returns on
    /// timeout so the caller can still try (the reveal step retries the funding gate anyway).
    /// </summary>
    public async Task WaitForSpendToSettleAsync(string walletId, IReadOnlySet<string> before, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = await SpendableBtcOutpointsAsync(walletId);
            if (before.Except(now).Any() && now.Except(before).Any())
                return;
            await Task.Delay(1500);
        }
    }

    /// <summary>The wallet's mnemonic, for a backup/reveal screen (HD wallets only; null otherwise).</summary>
    public async Task<string?> GetMnemonicAsync(string walletId)
    {
        var wallet = (await walletStorage.LoadAllWallets()).FirstOrDefault(w => w.Id == walletId);
        return wallet is { WalletType: WalletType.HD } ? wallet.Secret : null;
    }

    /// <summary>
    /// The wallet's stable login pubkey (x-only, hex) — derived from the mnemonic on a fixed
    /// path distinct from spending keys, so it survives restore and is the identity the game
    /// server knows you by ("sign in with your wallet"). Null if the wallet has no HD secret.
    /// </summary>
    public async Task<string?> GetLoginPubKeyHexAsync(string walletId)
    {
        var mnemonic = await GetMnemonicAsync(walletId);
        return mnemonic is null ? null : LoginPubKeyHex(mnemonic);
    }

    /// <summary>
    /// Sign the login-challenge digest (BIP340) with the stable login key, proving control of
    /// the login identity without revealing the key. Returns null if the wallet has no HD secret.
    /// Mirrors SelfCustodyWallet.SignLoginDigest so a browser wallet and a restored console
    /// wallet present the SAME identity to the server.
    /// </summary>
    public async Task<(string PubKeyHex, string SignatureHex)?> SignLoginAsync(string walletId, string nonceHex)
    {
        var mnemonic = await GetMnemonicAsync(walletId);
        return mnemonic is null ? null : SignLogin(mnemonic, nonceHex);
    }

    private static string LoginPubKeyHex(string mnemonic)
    {
        var pub = new byte[32];
        LoginKey(mnemonic).CreateXOnlyPubKey().WriteToSpan(pub);
        return Convert.ToHexString(pub).ToLowerInvariant();
    }

    private static (string PubKeyHex, string SignatureHex) SignLogin(string mnemonic, string nonceHex)
    {
        var key = LoginKey(mnemonic);
        var sig = key.SignBIP340(LoginChallenge.Digest(nonceHex));
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);
        var pub = new byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pub);
        return (Convert.ToHexString(pub).ToLowerInvariant(), Convert.ToHexString(sigBytes).ToLowerInvariant());
    }

    // Fixed login-key path (distinct from ark spending derivation), deterministic across
    // restores — identical to SelfCustodyWallet so the same words yield the same identity.
    private static ECPrivKey LoginKey(string mnemonic) =>
        ECPrivKey.Create(new Mnemonic(mnemonic).DeriveExtKey()
            .Derive(new KeyPath("83696968'/0'/0'")).PrivateKey.ToBytes());

    private static string NormalizeMnemonic(string mnemonic)
        => string.Join(' ', (mnemonic ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A player-facing wallet error (e.g. an invalid recovery phrase) surfaced by the UI.</summary>
public class GameWalletException(string message, Exception? inner = null) : Exception(message, inner);
