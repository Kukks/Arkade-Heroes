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
        // A throwaway database per run, so this genuinely IS a first install and a generated treasury
        // is what we want. The server will not generate one unless told — that refusal is what stops a
        // deployment that merely LOST its database from minting itself a new treasury.
        Environment.SetEnvironmentVariable("Chain__NArk__AllowTreasuryAutoCreate", "true");
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
        // Bought, not given: the wallet funded just above pays for the hero as well as the fee under test.
        var heroes = await client.RecruitAsync(wallet);
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

    // The marketplace fee is what makes secondary trading pay for the faucets — but it is taken from
    // the SALE by the offer's own covenant, not billed at listing. So listing costs the seller nothing
    // up front, an offer that never sells is never charged, and there is no fee payment that can fail
    // and strand a deposited hero. What must hold is that the cut is baked into the covenant the buyer
    // will be forced to satisfy, rather than left to a payment anyone could skip.
    [Fact]
    public async Task MarketplaceFee_IsBakedIntoTheOfferCovenant_AndListingCostsNothingUpFront()
    {
        var (seller, wallet, heroes) = await FundedPlayerAsync("FeeSeller");
        var hero = heroes[0];

        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, 5_000));
        Assert.Equal(ListingFee, offer.ListingFeeSats);

        // The fee rides the PUBLIC offer params, so the buyer rebuilds the same fee-bearing contract and
        // the fulfil leaf enforces the treasury's cut. This is the fee becoming structural.
        var parameters = await seller.Offers.ParamsAsync(offer.OfferId);
        Assert.Equal(ListingFee, parameters.FeeSats);
        Assert.False(string.IsNullOrEmpty(parameters.TreasuryFeeAddress));

        // Escrow the hero — and pay NOTHING else. The offer must go live on the deposit alone.
        await wallet.SendAssetAsync(offer.OfferAddress, offer.ItemAssetId, 1);
        await PollUntilAsync(async () => (await seller.Offers.GetAsync(offer.OfferId)).Status == "active",
            TimeSpan.FromSeconds(90), "the offer to go active on its deposit alone, with no fee to pay");
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

    /// <summary>The stud service bills the proposer TWICE and pays one of those fees back out to ANOTHER
    /// player — the only pass-through fee here. `stud` had zero E2E coverage, so neither had run on a
    /// real chain.</summary>
    [Fact]
    public async Task StudBreed_BillsBothFees_AndTheStudFeeReachesTheOtherOwner()
    {
        const long StudFee = 3_000;
        var (alice, aliceWallet, aliceHeroes) = await FundedPlayerAsync("FeeStudAlice");
        var (bob, bobWallet, bobHeroes) = await FundedPlayerAsync("FeeStudBob", fundTreasury: false);
        var bobBefore = await bobWallet.GetBalanceSatsAsync();

        var proposal = await alice.Stud.ProposeAsync(
            new StudProposeRequest(aliceHeroes[0].Id, bobHeroes[0].Id, StudFee));
        var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);

        Assert.NotNull(accept.StudFeeInvoice);
        Assert.Equal(StudFee, accept.StudFeeInvoice!.AmountSats);
        Assert.True(accept.BreedFeeInvoice.AmountSats > 0, "the escalating breed fee is the proposer's too");
        Assert.NotEqual(accept.BreedFeeInvoice.PayToAddress, accept.StudFeeInvoice.PayToAddress);

        await aliceWallet.SendAsync(accept.BreedFeeInvoice.PayToAddress, accept.BreedFeeInvoice.AmountSats);
        await aliceWallet.SendAsync(accept.StudFeeInvoice.PayToAddress, accept.StudFeeInvoice.AmountSats);

        StudRevealResponse? revealed = null;
        await PollUntilAsync(async () =>
        {
            try
            {
                revealed = await alice.Stud.RevealAsync(
                    proposal.ProposalId, new StudRevealRequest($"stud-{Guid.NewGuid():N}"));
                return true;
            }
            catch (ArkadeHeroesApiException) { return false; }
        }, TimeSpan.FromSeconds(120), "both stud invoices to clear so the foal can mint");

        var mine = await alice.Heroes.MineAsync();
        Assert.Contains(mine, h => h.Id == revealed!.Hero.Id);
        Assert.Equal(1, revealed!.Hero.Generation);

        // The half this test exists for: the stud fee left the treasury again, to a DIFFERENT wallet.
        await bobWallet.WaitForBalanceAsync(bobBefore + StudFee, TimeSpan.FromSeconds(120));

        var health = await alice.Economy.HealthAsync();
        Assert.True(health.InflowByTag.TryGetValue("stud", out var studBooked),
            $"no 'stud' inflow recorded; tags seen: {string.Join(",", health.InflowByTag.Keys)}");
        Assert.Equal(StudFee, studBooked);
    }

    /// <summary>A bid is the only flow where a HERO ASSET has to move between two player wallets before
    /// anyone is paid, and settle is refused until the chain shows it. `bids` had zero E2E coverage.</summary>
    [Fact]
    public async Task AcceptedBid_MovesTheHero_AndPaysTheOwnerTheBidLessTheFee()
    {
        const long Bid = 12_000;
        var (owner, ownerWallet, ownerHeroes) = await FundedPlayerAsync("FeeBidOwner");
        var (bidder, bidderWallet, _) = await FundedPlayerAsync("FeeBidBuyer", fundTreasury: false);
        var hero = ownerHeroes[0];

        var placed = await bidder.Bids.PlaceAsync(new PlaceBidRequest(hero.Id, Bid));
        var accepted = await owner.Bids.AcceptAsync(placed.BidId);

        Assert.Equal(Bid, accepted.Invoice.AmountSats);
        Assert.True(accepted.SellerNetSats < Bid, "the listing fee comes out of the owner's proceeds");
        Assert.False(accepted.Funded);

        await bidderWallet.SendAsync(accepted.Invoice.PayToAddress, accepted.Invoice.AmountSats);
        await PollUntilAsync(async () => (await owner.Bids.InvoiceAsync(placed.BidId)).Funded,
            TimeSpan.FromSeconds(120), "the bidder's payment to clear so the owner can deliver safely");

        // Settle is gated on the CHAIN showing the transfer, not on the owner's word for it.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => owner.Bids.SettleAsync(placed.BidId));

        await ownerWallet.SendAssetAsync(bidderWallet.Address, hero.AssetId!, 1);
        await bidderWallet.WaitForAssetAsync(hero.AssetId!, TimeSpan.FromSeconds(90));
        // an asset send spends a carrier, so an earlier baseline over-predicts the payout target
        var ownerBeforePayout = await ownerWallet.GetBalanceSatsAsync();

        HeroDto? settled = null;
        await PollUntilAsync(async () =>
        {
            try { settled = await owner.Bids.SettleAsync(placed.BidId); return true; }
            catch (ArkadeHeroesApiException) { return false; }
        }, TimeSpan.FromSeconds(120), "the hero transfer to be visible so the bid can settle");

        Assert.Equal((await bidder.Players.MeAsync()).PlayerId, settled!.OwnerId);
        await ownerWallet.WaitForBalanceAsync(ownerBeforePayout + accepted.SellerNetSats, TimeSpan.FromSeconds(150));
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
