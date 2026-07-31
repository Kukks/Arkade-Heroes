using ArkadeHeroes.Shared;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
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
///
/// <para>The READ methods below are <c>virtual</c> so a renderer outside a browser can substitute this
/// facade (see ArkadeHeroes.Tests.Web). They are the ones a page calls just to DRAW itself, and every one
/// of them bottoms out in NArk types — a server info record, a derived contract — that cannot be built
/// without a live Ark server. Without this seam no test can render the pages that own a wallet, which is
/// how a Send form that could only fail reached players. The keyword is the whole seam: there is one
/// implementation, WASM dispatches it the same way, and nothing here is overridden in the app.</para>
/// </summary>
public class GameWallet(
    IWalletStorage walletStorage,
    IClientTransport transport,
    ISpendingService spendingService,
    IVtxoStorage vtxoStorage,
    IContractService contractService)
{
    /// <summary>All wallets held in this browser (the game uses at most one).</summary>
    public virtual async Task<IReadOnlySet<ArkWalletInfo>> GetWalletsAsync()
        => await walletStorage.LoadAllWallets();

    /// <summary>The single active wallet, or null if the player hasn't created one yet.</summary>
    public virtual async Task<ArkWalletInfo?> GetActiveWalletAsync()
        => (await walletStorage.LoadAllWallets()).FirstOrDefault();

    public virtual async Task<bool> HasWalletAsync()
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
    public virtual async Task<string> GetReceiveAddressAsync(string walletId, CancellationToken ct = default)
    {
        var serverInfo = await transport.GetServerInfoAsync();
        var contract = await contractService.DeriveContract(walletId, NextContractPurpose.Receive);
        return contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);
    }

    /// <summary>Spendable balance in sats (sum of unlocked, in-bounds VTXOs). 0 on any sync error.</summary>
    public virtual async Task<long> GetBalanceAsync(string walletId)
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

    /// <summary>
    /// Removes a wallet from this browser. Irreversible from here: the recovery phrase is the only way
    /// back, and this deletes the copy that was holding the keys.
    ///
    /// <para>This is what signing out has to mean when the wallet IS the account. Clearing the session
    /// alone would be theatre — the next page load finds the wallet still sitting in storage and signs
    /// straight back in, which is worse than no sign-out at all on a shared machine.</para>
    /// </summary>
    public Task<bool> ForgetAsync(string walletId, CancellationToken ct = default) =>
        walletStorage.DeleteWallet(walletId, ct);

    /// <summary>The wallet's VTXOs (its coins and hero/asset carriers), newest first by default.</summary>
    public virtual async Task<IReadOnlyCollection<ArkVtxo>> GetVtxosAsync(string walletId, int skip = 0, int take = 50)
        => await vtxoStorage.GetVtxos(walletIds: [walletId], skip: skip, take: take);

    /// <summary>
    /// The distinct on-chain assets this wallet holds (heroes, XP, items) with total units each —
    /// grouped across all of the wallet's VTXO carriers. Mirrors SelfCustodyWallet.GetAssetsAsync.
    /// Returns empty on any sync error (the balance card degrades to sats-only).
    /// </summary>
    public virtual async Task<IReadOnlyList<(string AssetId, ulong Amount)>> GetAssetsAsync(string walletId)
    {
        try
        {
            var vtxos = await vtxoStorage.GetVtxos(walletIds: [walletId]);
            return vtxos
                .Where(v => v.Assets is { Count: > 0 })
                .SelectMany(v => v.Assets!)
                .GroupBy(a => a.AssetId)
                .Select(g => (g.Key, g.Aggregate(0UL, (sum, a) => sum + a.Amount)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

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

    /// <summary>How many times a spend re-selects and tries again after losing a race for its own coins.</summary>
    internal const int SpendAttempts = 3;

    /// <summary>Pause between spend attempts; scaled by the attempt number so the wallet backs off.</summary>
    internal static TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Builds, signs and submits the spend — retrying against FRESH coin state when it loses a race.
    ///
    /// <para>The tab runs the SDK's own batch services, so the wallet's coins can be settled away by its
    /// own background while the player is mid-flow. arkd then refuses the transaction
    /// (<c>VTXO_ALREADY_SPENT</c>, <c>VTXO_ALREADY_REGISTERED</c>, "temporarily locked") even though the
    /// player still holds the funds — the settlement handed them a NEW coin the tab hasn't caught up to.
    /// One attempt made that the player's problem: a raw SDK string in the UI and a fee that never got
    /// paid. So a refusal that names the coin state is treated as what it is — stale local knowledge —
    /// and healed: ask arkd which of the inputs it considers spent, write that truth into local storage,
    /// give the tab's own sync a beat to bring in the replacement, then select again from scratch.</para>
    ///
    /// <para>Only retried when arkd REFUSED the transaction, which is the whole point of matching on the
    /// coin-state errors rather than on failure in general: a refused transaction does not exist, so
    /// re-selecting cannot double-spend. Anything else surfaces immediately.</para>
    /// </summary>
    private async Task<string> SpendAsync(string walletId, ArkTxOut[] outputs)
    {
        for (var attempt = 1; ; attempt++)
        {
            ArkCoin[] coins;
            try
            {
                coins = await SelectSpendableCoinsAsync(walletId, outputs);
            }
            catch (GameWalletException) { throw; }
            catch (Exception ex)
            {
                throw new GameWalletException(
                    "Couldn't read your coins just now. Check your connection and try again.", ex);
            }

            try
            {
                var txId = await spendingService.Spend(walletId, coins, outputs);
                return txId.ToString();
            }
            catch (Exception ex) when (attempt < SpendAttempts && IsStaleCoinState(ex))
            {
                await ForgetSpentCoinsAsync(coins);
                await Task.Delay(RetryBackoff * attempt);
            }
            catch (GameWalletException) { throw; }
            catch (Exception ex) { throw new GameWalletException(Explain(ex), ex); }
        }
    }

    /// <summary>
    /// True when the failure says the inputs are no longer the wallet's to spend — arkd refused the
    /// transaction outright, so nothing was broadcast and a fresh selection is safe. Matched on the
    /// arkd error codes and the SDK's own locked-VTXO exception rather than on any failure, so a
    /// genuinely broken send is never quietly re-tried.
    /// </summary>
    private static bool IsStaleCoinState(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AlreadyLockedVtxoException) return true;
            var m = e.Message;
            if (m.Contains("VTXO_ALREADY_SPENT", StringComparison.OrdinalIgnoreCase)
                || m.Contains("VTXO_ALREADY_REGISTERED", StringComparison.OrdinalIgnoreCase)
                || m.Contains("already spent", StringComparison.OrdinalIgnoreCase)
                || m.Contains("already registered", StringComparison.OrdinalIgnoreCase)
                || m.Contains("temporarily locked", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Writes arkd's view of the attempted inputs into local storage, so the next selection stops
    /// offering coins the operator has already seen spent. Asking arkd directly (rather than waiting for
    /// the background sync) is what makes the retry converge instead of picking the same dead coin again.
    /// Best-effort: on any transport error the retry simply proceeds on what it has.
    /// </summary>
    private async Task ForgetSpentCoinsAsync(IReadOnlyCollection<ArkCoin> attempted)
    {
        try
        {
            var outpoints = attempted.Select(c => c.Outpoint).ToList();
            await foreach (var vtxo in transport.GetVtxosByOutpoints(outpoints, spentOnly: true))
                await vtxoStorage.UpsertVtxo(vtxo);
        }
        catch { /* the delay + re-selection still give the tab's own sync a chance to catch up */ }
    }

    /// <summary>
    /// A spend failure in the player's language. The SDK speaks gRPC status text and arkd speaks error
    /// codes — "net_http_message_not_success_statuscode_reason, 400/409" told a player nothing except
    /// that the game was broken. The detail is not lost: it rides on the inner exception for the console.
    /// </summary>
    private static string Explain(Exception ex) => IsStaleCoinState(ex)
        ? "Your coins are being settled by the network right now. Give it a few seconds and try again."
        : "That payment didn't go through. Your funds are safe — please try again in a moment.";

    // Explicit coin selection mirroring SelfCustodyWallet: exclude recoverable (swept/expired)
    // coins, cover each output asset with its carrier VTXOs, then the sats with the largest
    // pure-BTC coins plus one for headroom (fee + change).
    private async Task<ArkCoin[]> SelectSpendableCoinsAsync(string walletId, ArkTxOut[] outputs)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var available = await spendingService.GetAvailableCoins(walletId);
        var spendable = available.Where(c => c.CanSpendOffchain(now)).ToList();
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

        // A settlement round can consolidate the whole wallet into ONE VTXO that carries the sats
        // AND every hero the player owns. After that there is no pure-BTC coin left, so no fee could
        // ever be paid again — recruit, breed, stake and buy all fail with "not enough spendable
        // sats" while the balance pill still shows the full amount. Fall back to spending a carrier:
        // the SDK's asset packet assigns every unspent input asset to the change output, so the
        // heroes ride home to the player's own change address. Only take a carrier that leaves change
        // at or above dust — below dust the change output moves to vout 0 while asset change still
        // points at the LAST output, which would hand the player's heroes to the recipient.
        if (selected.Sum(c => c.TxOut.Value.Satoshi) < required)
        {
            var dust = (await transport.GetServerInfoAsync()).Dust.Satoshi;
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
        {
            // Distinguish the settlement-lag case (funds are present but a freshly-received VTXO
            // hasn't settled into a round yet, so CanSpendOffchain is still false) from a genuine
            // shortfall — the old blanket "not enough sats" was misleading when the balance showed funds.
            var availableBtc = available.Where(c => c.Assets is null or { Count: 0 }).Sum(c => c.TxOut.Value.Satoshi);
            throw new GameWalletException(availableBtc >= required
                ? "Your funds are still settling — try again in a moment."
                : "Not enough spendable sats (you need funds for the amount plus the network fee).");
        }

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

/// <summary>
/// A covenant action funded its escrow but the reveal hasn't settled within the wait window.
/// Carries the pending commit id (<see cref="PendingId"/> — the breeding/merge id) so the UI can
/// retry ONLY the reveal: the inputs are already escrowed, so re-running the whole flow would try
/// to re-deposit spent parents. Subclasses <see cref="GameWalletException"/> so existing catch-alls
/// still surface the message; pages that want the retry affordance catch this type specifically.
/// </summary>
public sealed class RevealPendingException(string pendingId, string message) : GameWalletException(message)
{
    public string PendingId { get; } = pendingId;
}
