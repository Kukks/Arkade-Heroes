using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The recovery list: this player's covenant escrows that may still hold their assets with no path
/// forward. It exists so a stranded deposit can be SEEN at all — the console client has reclaim
/// commands (canceloffer / refund-breed / refund-merge), the browser has none, so a listing stuck on an
/// unpaid fee was previously invisible there. The list is discovery only: reclaiming is a covenant spend
/// from the player's own wallet against the public escrow params, so it never needs the server to agree.
/// </summary>
public class ReclaimableTests
{
    const long Fee = 500;

    static WebApplicationFactory<Program> FeeFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = Fee)));

    [Fact]
    public async Task DepositedListing_IsReclaimable()
    {
        // The seller's asset is escrowed in a resting offer. Whether it sells or not, that asset is
        // recoverable through the covenant's timelocked reclaim leaf, and the browser needs to SEE it —
        // it has no other way to find a listing it wants back.
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Resting");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        var resting = Assert.Single(await seller.Players.ReclaimableAsync());
        Assert.Equal("offer", resting.Kind);
        Assert.Equal(offer.OfferId, resting.Id);
        Assert.Contains("resting on the market", resting.Summary);
    }

    [Fact]
    public async Task Listing_StillAwaitingItsDeposit_IsNotReclaimable()
    {
        // Nothing has left the wallet yet, so there is nothing to recover — listing it would be noise.
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Undeposited");
        await seller.BuyItemAsync("rusty-blade");
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));

        Assert.Empty(await seller.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task Reclaimable_IsScopedToTheAskingPlayer()
    {
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Mine");
        var (other, _) = await factory.RegisterAsync("RC-Theirs");
        await seller.BuyItemAsync("rusty-blade");
        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        Assert.Single(await seller.Players.ReclaimableAsync());
        Assert.Empty(await other.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task UnrevealedCovenantBreed_IsReclaimable()
    {
        // A covenant breed that never reached reveal. It is listed on the SESSION being unfinished, not
        // on the escrow reading fully funded: a run that died between the two parent deposits leaves a
        // hero escrowed while IsBreedEscrowFundedAsync still reports false, and that is the case that
        // most needs surfacing. Reclaiming an empty escrow is harmless; hiding a full one is not.
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("RC-Breed");
        var heroes = await player.ClaimStartersAsync();

        var commit = await player.Breeding.CommitAsync(
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));

        var stuck = Assert.Single(await player.Players.ReclaimableAsync());
        Assert.Equal("breed", stuck.Kind);
        Assert.Equal(commit.BreedingId, stuck.Id);
    }

    [Fact]
    public async Task StakedCovenantWager_IsReclaimable()
    {
        // A wagered duel whose stake is in the per-party escrow. Real sats, and the only way back is the
        // covenant's timelocked refund leaf — the console has had `refund <matchId>` for ages, so the
        // browser needs to be able to SEE the stake to spend the same leaf.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("RC-Wager-A");
        var (bob, _) = await factory.RegisterAsync("RC-Wager-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(a[0].Id, b[0].Id, 4_000, "covenant"));
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        var stuck = Assert.Single(await alice.Players.ReclaimableAsync());
        Assert.Equal("wager", stuck.Kind);
        Assert.Equal(open.MatchId, stuck.Id);
        Assert.Contains("4000-sat stake", stuck.Summary);
    }

    [Fact]
    public async Task CovenantWager_ListsOnlyTheSideThatActuallyStaked()
    {
        // The wager's escrows are PER-PARTY, so "is the escrow funded" is the wrong question — the right
        // one is whose stake is in it. Bob accepted and so is a party to the match, but never staked: he
        // has nothing to recover, and a row offering him one could only ever fail.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("RC-WagerSide-A");
        var (bob, _) = await factory.RegisterAsync("RC-WagerSide-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(a[0].Id, b[0].Id, 4_000, "covenant"));
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        await bob.Matches.AcceptAsync(open.MatchId);

        Assert.Equal("wager", Assert.Single(await alice.Players.ReclaimableAsync()).Kind);
        Assert.Empty(await bob.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task SettledCovenantWager_IsNotReclaimable()
    {
        // The duel resolved, so the escrows were SWEPT by the settle — nothing is left to reclaim for
        // either side. This is gated on the match's own status rather than on a funding probe, and it has
        // to be: a settled escrow's per-party funding can still read as staked, so a funding-only gate
        // would leave every finished duel on this page forever behind a button that cannot work.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("RC-WagerSettled-A");
        var (bob, _) = await factory.RegisterAsync("RC-WagerSettled-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(a[0].Id, b[0].Id, 4_000, "covenant"));
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        await alice.Matches.FightAsync(open.MatchId, new FightRequest("rc-settled"));

        Assert.Empty(await alice.Players.ReclaimableAsync());
        Assert.Empty(await bob.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task HalfFundedDeathMatch_IsReclaimableByTheHeroesStaker()
    {
        // The death-match escrow is JOINT — one address, both heroes — but its reclaim leaf is PER SIDE and
        // purely structural, so the staker recovers their hero even though the opponent never showed. That
        // is why this must NOT be gated on IsDeathMatchEscrowFundedAsync, which is true only once BOTH
        // heroes are in: the half-funded escrow is precisely the one holding a hero with no way forward.
        // Bob, who never accepted, still holds his hero and so has nothing here.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("RC-DM-A");
        var (bob, _) = await factory.RegisterAsync("RC-DM-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        await alice.Dev.FundDeathMatchEscrowAsync(
            new { DeathMatchId = open.DeathMatchId, Role = "challenger" });

        var stuck = Assert.Single(await alice.Players.ReclaimableAsync());
        Assert.Equal("deathmatch", stuck.Kind);
        Assert.Equal(open.DeathMatchId, stuck.Id);
        Assert.Contains(a[0].Name, stuck.Summary);
        Assert.Empty(await bob.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task SettledDeathMatch_IsNotReclaimableByEitherSide()
    {
        // The death-match counterpart of SettledCovenantWager_IsNotReclaimable, and the sharper of the two:
        // a settle BURNS the losing hero, so a row left behind here would offer to recover a hero that no
        // longer exists. Gated on the session's own Completed flag rather than on a funding probe, for the
        // same reason the duel is gated on status — the escrow's funding can still read as staked after the
        // settle has swept it, so a funding-only gate would strand every finished death-match on this page
        // forever behind a button that cannot work.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("RC-DMSettled-A");
        var (bob, _) = await factory.RegisterAsync("RC-DMSettled-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        // Both stake their hero AND pay the per-character death-match fee — settle refuses without both.
        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        await alice.Dev.FundDeathMatchEscrowAsync(
            new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(
            new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("rc-dm-settled"));

        Assert.Empty(await alice.Players.ReclaimableAsync());
        Assert.Empty(await bob.Players.ReclaimableAsync());
    }
}
