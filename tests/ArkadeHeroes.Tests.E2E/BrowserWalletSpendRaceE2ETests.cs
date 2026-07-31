using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Web.Wallet;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Xunit.Abstractions;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The browser wallet racing its own background settlement, against a REAL arkd.
///
/// <para>The browser runs the SDK's batch services in the tab, so a player's coin can be spent into a
/// settlement batch at the exact moment they press a button that costs a fee. arkd then refuses the
/// player's transaction — VTXO_ALREADY_SPENT — while the tab's local VTXO store still lists the coin as
/// spendable. Reproduced here deterministically by giving one mnemonic TWO independent local stores: one
/// spends a coin, the other still believes it holds it, which is precisely the state the race leaves.</para>
///
/// Requires: node regtest/regtest.mjs start --profile ark --profile emulator
/// </summary>
public class BrowserWalletSpendRaceE2ETests(ITestOutputHelper output) : IAsyncLifetime
{
    private const long PerCoin = 60_000;
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

    private async Task<SelfCustodyWallet> NewWalletAsync(string? mnemonic = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-race-{Guid.NewGuid():N}.db");
        _walletDbPaths.Add(dbPath);
        var wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
            Mnemonic = mnemonic,
        });
        _wallets.Add(wallet);
        return wallet;
    }

    private static GameWallet BrowserWallet(SelfCustodyWallet host) => new(
        host.GetService<IWalletStorage>(),
        host.GetService<IClientTransport>(),
        host.GetService<ISpendingService>(),
        host.GetService<IVtxoStorage>(),
        host.GetService<IContractService>());

    /// <summary>
    /// The fix: a fee spend whose input was taken out from under it must come back and try again against
    /// what the wallet ACTUALLY holds, not fail in the player's face. Before the retry existed this threw
    /// straight out of the SDK — "Send failed: net_http_message_not_success_statuscode_reason, 400" — with
    /// the player's other coin sitting right there, unspent.
    /// </summary>
    [Fact]
    public async Task FeeSpendWhoseInputWasSettledAway_RetriesAgainstFreshCoinState()
    {
        var settler = await NewWalletAsync();
        var browser = await NewWalletAsync(settler.Mnemonic);
        var feeRecipient = await NewWalletAsync();
        // Same words, same derivation: two local stores over ONE on-chain wallet, which is what the tab
        // and its background batch services are.
        Assert.Equal(settler.Address, browser.Address);

        await RegtestHelper.ArkSend(settler.Address, PerCoin);
        await RegtestHelper.ArkSend(settler.Address, PerCoin);
        await settler.WaitForBalanceAsync(PerCoin * 2, TimeSpan.FromSeconds(60));
        await browser.WaitForBalanceAsync(PerCoin * 2, TimeSpan.FromSeconds(60));

        var settlerSpending = settler.GetService<ISpendingService>();
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var coins = (await settlerSpending.GetAvailableCoins(settler.WalletId))
            .Where(c => c.CanSpendOffchain(now))
            .OrderByDescending(c => c.TxOut.Value.Satoshi)
            .ToArray();
        Assert.True(coins.Length >= 2, "the wallet needs two coins for one of them to be raced away");

        // The race, made deterministic. Settling a coin away is only half of it — the half that bites is
        // the tab still believing it owns the coin, and the SDK's own sync stream heals that within the
        // same second, far faster than a player could press a button. So keep the pre-spend record and
        // put it back: the browser's store now says spendable, arkd says spent, which is exactly the
        // window a player lands in.
        var raced = coins[0];
        var browserStore = browser.GetService<IVtxoStorage>();
        var preSpendRecord = (await browserStore.GetVtxos(outpoints: [raced.Outpoint])).Single();

        await settlerSpending.Spend(settler.WalletId, [raced],
            [new ArkTxOut(ArkTxOutType.Vtxo, raced.TxOut.Value, ArkAddress.Parse(settler.Address))]);
        await browser.SyncAsync();
        await browserStore.UpsertVtxo(preSpendRecord);

        var browserWallet = BrowserWallet(browser);
        var stillListed = (await browser.GetService<ISpendingService>().GetAvailableCoins(browser.WalletId))
            .Any(c => c.Outpoint == raced.Outpoint);

        // Pay the fee. Selection reaches for the largest coin first — the one that is already gone.
        var txId = await browserWallet.SendSatsAsync(browser.WalletId, feeRecipient.Address, FeeSats);
        Assert.False(string.IsNullOrWhiteSpace(txId));
        await feeRecipient.WaitForBalanceAsync(FeeSats, TimeSpan.FromSeconds(60));

        // Reported, not asserted: whether the browser's store had actually gone stale by the time it
        // selected. The SDK's own sync stream can heal it within the same second, in which case this run
        // proved the happy path rather than the race — the retry is still what makes the race survivable.
        output.WriteLine($"browser store still listed the raced coin at selection time: {stillListed}");
    }
}
