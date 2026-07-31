using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Web.Wallet;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The BROWSER wallet's coin selection, against a REAL arkd — the one path no unit test can prove,
/// because getting it wrong sends a player's heroes to the fee recipient instead of back to them.
///
/// <para>A settlement batch consolidates a wallet into ONE VTXO that carries the sats AND every hero.
/// From there every fee the game charges (recruit, breed, stake, buy, tournament buy-in) has to be paid
/// out of an asset-bearing coin or not at all. This drives that exact shape end to end: fund, issue an
/// asset, fold both into a single carrier, then pay a fee out of it and check the asset came home.</para>
///
/// <para><see cref="GameWallet"/> is constructed over a <see cref="SelfCustodyWallet"/>'s NArk service
/// graph — the same five interfaces Program.cs hands it in the browser — so this exercises the shipping
/// facade, not a re-implementation of it.</para>
///
/// Requires: node regtest/regtest.mjs start --profile ark --profile emulator
/// </summary>
public class BrowserWalletCarrierSpendE2ETests : IAsyncLifetime
{
    private const long Funding = 100_000;
    private const long FeeSats = 1_000;

    private readonly List<string> _walletDbPaths = [];
    private readonly List<SelfCustodyWallet> _wallets = [];

    public async Task InitializeAsync() =>
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

    public async Task DisposeAsync()
    {
        foreach (var wallet in _wallets) await wallet.DisposeAsync();
        foreach (var path in _walletDbPaths)
            try { if (File.Exists(path)) File.Delete(path); } catch { /* locked on Windows is fine */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-carrier-{Guid.NewGuid():N}.db");
        _walletDbPaths.Add(dbPath);
        var wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
        _wallets.Add(wallet);
        return wallet;
    }

    /// <summary>The browser's wallet facade over a live NArk graph — the five services Program.cs injects.</summary>
    private static GameWallet BrowserWallet(SelfCustodyWallet host) => new(
        host.GetService<IWalletStorage>(),
        host.GetService<IClientTransport>(),
        host.GetService<ISpendingService>(),
        host.GetService<IVtxoStorage>(),
        host.GetService<IContractService>());

    private static async Task<T> PollAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } hit) return hit;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// Folds every spendable coin the wallet holds into a SINGLE VTXO that carries all of the sats and
    /// the asset — the state a settlement batch leaves behind, reproduced deterministically.
    /// </summary>
    private static async Task<ArkCoin> ConsolidateOntoOneCarrierAsync(
        SelfCustodyWallet host, string assetId, ulong assetAmount)
    {
        var spending = host.GetService<ISpendingService>();
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var coins = (await spending.GetAvailableCoins(host.WalletId))
            .Where(c => c.CanSpendOffchain(now)).ToArray();
        Assert.True(coins.Length > 1, "expected the funding coin and the issuance carrier to be separate");

        var total = coins.Sum(c => c.TxOut.Value.Satoshi);
        ArkTxOut[] outputs =
        [
            new ArkTxOut(ArkTxOutType.Vtxo, Money.Satoshis(total), ArkAddress.Parse(host.Address))
            {
                Assets = [new ArkTxOutAsset(assetId, assetAmount)],
            },
        ];
        await spending.Spend(host.WalletId, coins, outputs);

        return await PollAsync(async () =>
        {
            await host.SyncAsync();
            var live = (await spending.GetAvailableCoins(host.WalletId))
                .Where(c => c.CanSpendOffchain(new TimeHeight(DateTimeOffset.UtcNow, 0))).ToList();
            return live.Count == 1 && live[0].Assets is { Count: > 0 } ? live[0] : null;
        }, TimeSpan.FromSeconds(90), "the wallet to consolidate onto one asset-bearing coin");
    }

    /// <summary>
    /// The defect, and the fix: a wallet whose whole balance rides on ONE hero-carrying VTXO must still
    /// be able to pay a fee — and the hero must land back in the player's own wallet, never on the fee
    /// recipient. Coin selection may only reach for a carrier when no pure-BTC coin can cover the spend,
    /// and only when the change it leaves behind is a real VTXO (at or above dust): below dust the SDK
    /// moves BTC change to vout 0 while asset change still points at the LAST output, which is the
    /// recipient.
    /// </summary>
    [Fact]
    public async Task FeePaidFromTheOnlyCoin_LandsTheAssetBackInTheWallet_NotOnTheFeeRecipient()
    {
        var host = await NewWalletAsync();
        var feeRecipient = await NewWalletAsync();
        var browser = BrowserWallet(host);

        await RegtestHelper.ArkSend(host.Address, Funding);
        await host.WaitForBalanceAsync(Funding, TimeSpan.FromSeconds(60));

        var issuance = await host.GetService<IAssetManager>()
            .IssueAsync(host.WalletId, new IssuanceParams(1));
        await host.WaitForAssetAsync(issuance.AssetId, TimeSpan.FromSeconds(60));

        var carrier = await ConsolidateOntoOneCarrierAsync(host, issuance.AssetId, 1);
        // The premise the rest of the test rests on: no pure-BTC coin is left, so the fee CANNOT be paid
        // without spending the carrier. If this ever stops holding the test proves nothing.
        Assert.NotNull(carrier.Assets);
        Assert.True(carrier.TxOut.Value.Satoshi > FeeSats, "the carrier must be able to cover the fee");

        var txId = await browser.SendSatsAsync(host.WalletId, feeRecipient.Address, FeeSats);
        Assert.False(string.IsNullOrWhiteSpace(txId));

        // arkd accepted it: the fee arrived, and — the part that matters — the hero rode home on the
        // change output rather than following the sats to the fee recipient.
        await feeRecipient.WaitForBalanceAsync(FeeSats, TimeSpan.FromSeconds(60));
        await PollAsync(async () =>
        {
            await host.SyncAsync();
            var held = await browser.GetAssetsAsync(host.WalletId);
            return held.Any(a => a.AssetId == issuance.AssetId && a.Amount == 1) ? "held" : null;
        }, TimeSpan.FromSeconds(90), "the asset to settle back into the player's own wallet");

        var stolen = await feeRecipient.GetAssetsAsync();
        Assert.DoesNotContain(stolen, a => a.AssetId == issuance.AssetId);
    }
}
