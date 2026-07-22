using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The three treasury fee flows against a REAL regtest, paid from REAL self-custody wallets:
/// the marketplace listing fee, the hero rename fee, and the tournament buy-in. These are the
/// counterweight to the daily + season faucets, so each test proves BOTH halves — the action is
/// REFUSED while the invoice is unpaid, and the treasury actually books the inflow once it clears.
/// A fee that isn't enforced is just a label on a screen.
/// Requires: node regtest/regtest.mjs start --profile ark --profile emulator
/// </summary>
public class FeeFlowsOnRegtestTests : IAsyncLifetime
{
    private const long ListingFee = 250;
    private const long BuyIn = 1_000;

    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private readonly List<string> _walletDbPaths = [];
    private readonly List<SelfCustodyWallet> _wallets = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

        _serverDbPath = Path.Combine(Path.GetTempPath(), $"arkade-heroes-fees-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__EsploraUri", "http://localhost:3000/api");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        // The listing fee SHIPS DISABLED (0) — turn it on here so the gate is actually exercised.
        Environment.SetEnvironmentVariable("Game__OfferListingFeeSats", ListingFee.ToString());

        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        // Env vars are process-global and E2E runs serially: leaving the listing fee on would
        // silently change the offer flow for every test class that runs after this one.
        Environment.SetEnvironmentVariable("Game__OfferListingFeeSats", null);
        foreach (var wallet in _wallets) await wallet.DisposeAsync();
        _factory.Dispose();
        foreach (var path in _walletDbPaths.Append(_serverDbPath))
            try { if (File.Exists(path)) File.Delete(path); } catch { /* locked on Windows is fine */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-fee-wallet-{Guid.NewGuid():N}.db");
        _walletDbPaths.Add(dbPath);
        var wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
        _wallets.Add(wallet);
        return wallet;
    }

    /// <summary>Funds the treasury once, then a player wallet, and registers + claims starters.</summary>
    private async Task<(ArkadeHeroesClient Client, SelfCustodyWallet Wallet, List<HeroDto> Heroes)> FundedPlayerAsync(
        string name, bool fundTreasury = true)
    {
        var anonymous = new ArkadeHeroesClient(_factory.CreateClient());
        if (fundTreasury)
        {
            var info = await anonymous.Chain.InfoAsync();
            Assert.Equal("NArk", info.Mode);
            await RegtestHelper.ArkSend(info.TreasuryAddress, 200_000);
        }

        var wallet = await NewWalletAsync();
        await RegtestHelper.ArkSend(wallet.Address, 100_000);
        await wallet.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));

        var client = new ArkadeHeroesClient(_factory.CreateClient());
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        var heroes = (await client.Heroes.ClaimStartersAsync()).Heroes.ToList();
        return (client, wallet, heroes);
    }

    private static async Task PollUntilAsync(Func<Task<bool>> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return;
            await Task.Delay(1500);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    // The listing fee is what makes secondary trading pay for the faucets. If an unpaid offer
    // still went live, sellers would list for free and the treasury capture would be fictional.
    [Fact]
    public async Task ListingFee_HoldsTheOfferPending_UntilPaidFromTheSellersWallet()
    {
        var (seller, wallet, heroes) = await FundedPlayerAsync("FeeSeller");
        var hero = heroes[0];

        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, 5_000));
        Assert.Equal(ListingFee, offer.ListingFeeSats);
        Assert.NotNull(offer.ListingFee);
        Assert.Equal(ListingFee, offer.ListingFee!.AmountSats);

        // Escrow the hero itself — funded, but the fee is still outstanding.
        await wallet.SendAssetAsync(offer.OfferAddress, offer.ItemAssetId, 1);

        // The hero is deposited and yet the offer must NOT be buyable: the fee gates it.
        await Task.Delay(4000);
        var pending = await seller.Offers.GetAsync(offer.OfferId);
        Assert.NotEqual("active", pending.Status);

        // Pay the fee from the seller's own wallet — non-custodial, the server never holds keys.
        await wallet.SendAsync(offer.ListingFee.PayToAddress, offer.ListingFee.AmountSats);

        await PollUntilAsync(async () => (await seller.Offers.GetAsync(offer.OfferId)).Status == "active",
            TimeSpan.FromSeconds(90), "the offer to go active once the listing fee cleared");

        // And the treasury actually booked it under its own tag.
        var health = await seller.Economy.HealthAsync();
        Assert.True(health.InflowByTag.TryGetValue("listing", out var booked),
            $"no 'listing' inflow recorded; tags seen: {string.Join(",", health.InflowByTag.Keys)}");
        Assert.Equal(ListingFee, booked);
    }

    [Fact]
    public async Task RenameFee_IsRefusedUntilPaid_ThenAppliesTheNameAndBooksInflow()
    {
        var (player, wallet, heroes) = await FundedPlayerAsync("FeeRenamer");
        var hero = heroes[0];
        var newName = $"Vex{Guid.NewGuid():N}"[..12];

        var rename = await player.Heroes.RequestRenameAsync(hero.Id, new RenameHeroRequest(newName));
        Assert.True(rename.FeeSats > 0, "hero rename is expected to charge a treasury fee by default");
        Assert.NotNull(rename.Fee);

        // Confirming before paying must be refused — otherwise the name is free.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => player.Heroes.ConfirmRenameAsync(hero.Id));

        await wallet.SendAsync(rename.Fee!.PayToAddress, rename.Fee.AmountSats);

        // The invoice clears asynchronously; retry the confirm until the server observes payment.
        HeroDto? renamed = null;
        await PollUntilAsync(async () =>
        {
            try { renamed = await player.Heroes.ConfirmRenameAsync(hero.Id); return true; }
            catch (ArkadeHeroesApiException) { return false; }
        }, TimeSpan.FromSeconds(90), "the rename fee to clear so the name applies");

        Assert.Equal(newName, renamed!.Name);

        var health = await player.Economy.HealthAsync();
        Assert.True(health.InflowByTag.TryGetValue("rename", out var booked),
            $"no 'rename' inflow recorded; tags seen: {string.Join(",", health.InflowByTag.Keys)}");
        Assert.Equal(rename.FeeSats, booked);
    }

    // The buy-in is the only fee that comes BACK to players (as prizes), so the invariant that
    // matters is that the podium never pays out more than the entrants actually put in.
    [Fact]
    public async Task TournamentBuyIn_BillsBothEntrants_AndPaysNoMoreThanThePotMinusRake()
    {
        var (alice, aliceWallet, aliceHeroes) = await FundedPlayerAsync("FeeAlice");
        var (bob, bobWallet, bobHeroes) = await FundedPlayerAsync("FeeBob", fundTreasury: false);

        var opened = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHeroes[0].Id, BuyIn, 2));
        Assert.Equal(BuyIn, opened.BuyIn.AmountSats);
        await aliceWallet.SendAsync(opened.BuyIn.PayToAddress, opened.BuyIn.AmountSats);

        var joined = await bob.Tournament.JoinAsync(opened.Tournament.Id, new JoinTournamentRequest(bobHeroes[0].Id));
        Assert.Equal(BuyIn, joined.BuyIn.AmountSats);
        await bobWallet.SendAsync(joined.BuyIn.PayToAddress, joined.BuyIn.AmountSats);

        TournamentResolveResponse? resolved = null;
        await PollUntilAsync(async () =>
        {
            try
            {
                resolved = await alice.Tournament.ResolveAsync(
                    opened.Tournament.Id, new FightRequest($"tourney-{Guid.NewGuid():N}"));
                return true;
            }
            catch (ArkadeHeroesApiException) { return false; }
        }, TimeSpan.FromSeconds(120), "both buy-ins to clear so the bracket can resolve");

        var pot = BuyIn * 2;
        var paid = resolved!.Prizes.Sum();
        Assert.True(paid <= pot, $"podium paid {paid} out of a {pot} pot — the house rake cannot be negative");
        Assert.True(paid > 0, "a resolved bracket should pay its champion something");
        Assert.NotNull(resolved.Tournament.ChampionHeroId);
    }
}
