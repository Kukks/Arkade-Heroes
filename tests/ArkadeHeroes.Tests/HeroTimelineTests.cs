using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A hero's provenance timeline: how it was born, what it fought, what it was traded for, what was burned
/// to make it.
///
/// Almost all of it is DERIVED from facts the arena already keeps — the progression receipt ledger files
/// every breed, fusion, absorb, duel, spar, death-match, gauntlet and trials run under each hero it names,
/// and a hero's lineage columns carry its parents. So the load-bearing question is not "does an event show
/// up" but ATTRIBUTION: a receipt names two heroes and is filed under both, a squad relay files three at
/// once, and a fusion files one under heroes that no longer exist. Getting that wrong would put another
/// hero's history on this hero's page, which is worse than showing nothing.
///
/// The one thing no derivation could recover is what a hero SOLD for. A sold offer is closed, closed rows
/// are never rehydrated, `closed` cannot tell a sale from the seller pulling their own listing, and the
/// buyer was never written anywhere — so that alone gets a durable row, and it is held to the money-path
/// bar the rest of the marketplace is: it must survive a restart and it must not double-count.
/// </summary>
public class HeroTimelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HeroTimelineTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static HeroTimelineEventDto? Find(HeroTimelineDto tl, string kind) =>
        tl.Events.FirstOrDefault(e => e.Kind == kind);

    // ── Attribution ────────────────────────────────────────────────────

    [Fact]
    public async Task Birth_NamesBothParents_OnTheChild_AndTheBreedOnEachParent()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-Breeder");
        var heroes = await alice.ClaimStartersAsync();
        var (parentA, parentB) = (heroes[0], heroes[1]);

        var (_, reveal) = await alice.BreedAsync(parentA.Id, parentB.Id, "tl-breed-nonce");
        var child = reveal.Hero;

        // The CHILD's origin names both parents, by name (they are both still alive).
        var childTl = await alice.Heroes.TimelineAsync(child.Id);
        var born = Find(childTl, "bred");
        Assert.NotNull(born);
        Assert.Equal(
            new[] { parentA.Id, parentB.Id }.OrderBy(x => x, StringComparer.Ordinal),
            born!.Related.Select(r => r.HeroId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(born.Related, r => Assert.NotNull(r.Name));
        Assert.Contains(parentA.Name, born.Summary);
        Assert.Contains(parentB.Name, born.Summary);

        // …and each PARENT records the same breed from its own side, naming the child.
        foreach (var (mine, mate) in new[] { (parentA, parentB), (parentB, parentA) })
        {
            var parentTl = await alice.Heroes.TimelineAsync(mine.Id);
            var bredWith = Find(parentTl, "bred-with");
            Assert.NotNull(bredWith);
            Assert.Contains(mate.Name, bredWith!.Summary);
            Assert.Contains(child.Name, bredWith.Summary);
            // A parent is NOT born of its own breed — that event belongs to the child alone.
            Assert.DoesNotContain(parentTl.Events, e => e.Kind == "bred");
        }
    }

    [Fact]
    public async Task AnUninvolvedHero_GetsNoneOfAnotherHerosHistory()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-Involved");
        var (bob, _) = await _factory.RegisterAsync("TL-Bystander");
        var heroes = await alice.ClaimStartersAsync();
        var bystander = (await bob.ClaimStartersAsync())[0];

        // Noise across EVERY receipt shape the timeline renders, none of it the bystander's. The solo
        // runs (gauntlet, trials) are the load-bearing ones: they name a single hero and carry an empty
        // HeroBId, so an attribution check that only compared the "other" side would wave them straight
        // through onto anyone's page.
        var (_, reveal) = await alice.BreedAsync(heroes[0].Id, heroes[1].Id, "tl-noise-nonce");
        var duel = await alice.Matches.OpenAsync(new OpenMatchRequest(heroes[0].Id, heroes[1].Id));
        await alice.Matches.FightAsync(duel.MatchId, new FightRequest("tl-noise-fight"));
        var gauntlet = await alice.Gauntlet.OpenAsync(heroes[0].Id);
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = gauntlet.FeeInvoice.InvoiceId });
        await alice.Gauntlet.RunAsync(gauntlet.GauntletId, "tl-noise-gauntlet");
        var trials = await alice.Trials.OpenAsync(heroes[1].Id);
        await alice.Trials.RunAsync(trials.TrialsId, "tl-noise-trials");

        var bystanderTl = await bob.Heroes.TimelineAsync(bystander.Id);

        // Its ONLY event is its own birth — nothing from a breed or a fight it was never in.
        var only = Assert.Single(bystanderTl.Events);
        Assert.Equal("born", only.Kind);
        // Belt and braces: no event anywhere on its page names a hero from the other player's story.
        var foreignIds = new[] { heroes[0].Id, heroes[1].Id, reveal.Hero.Id };
        Assert.DoesNotContain(bystanderTl.Events,
            e => e.Related.Any(r => foreignIds.Contains(r.HeroId)) || foreignIds.Any(e.Summary.Contains));
        Assert.DoesNotContain(bystanderTl.Events, e => e.WatchMatchId == duel.MatchId);
    }

    [Fact]
    public async Task ADuel_IsAttributedToBothFighters_WithOpposedOutcomes_AndLinksItsReplay()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-DuelA");
        var (bob, _) = await _factory.RegisterAsync("TL-DuelB");
        var mine = (await alice.ClaimStartersAsync())[0];
        var theirs = (await bob.ClaimStartersAsync())[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(mine.Id, theirs.Id));   // friendly
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("tl-duel-nonce"));

        var mineTl = await alice.Heroes.TimelineAsync(mine.Id);
        var theirsTl = await bob.Heroes.TimelineAsync(theirs.Id);
        var a = Find(mineTl, "spar");
        var b = Find(theirsTl, "spar");
        Assert.NotNull(a);
        Assert.NotNull(b);

        // The SAME fight, on both pages, told from each hero's own side.
        Assert.Equal(open.MatchId, a!.WatchMatchId);
        Assert.Equal(open.MatchId, b!.WatchMatchId);
        Assert.NotEqual(a.Outcome, b.Outcome);
        var winnerIsMine = fight.Result.WinnerId == mine.Id;
        Assert.Equal(winnerIsMine ? "won" : "lost", a.Outcome);
        Assert.Equal(winnerIsMine ? "lost" : "won", b.Outcome);
        // Each page names the OTHER hero, never itself.
        Assert.Equal(theirs.Id, Assert.Single(a.Related).HeroId);
        Assert.Equal(mine.Id, Assert.Single(b.Related).HeroId);
    }

    [Fact]
    public async Task ASquadRelaySlot_GetsNoReplayLink_BecauseWatchCannotServeOne()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-SquadA");
        var (bob, _) = await _factory.RegisterAsync("TL-SquadB");
        var mine = (await alice.RecruitAsync(4)).Take(3).Select(h => h.Id).ToList();
        var theirs = (await bob.RecruitAsync(4)).Take(3).Select(h => h.Id).ToList();

        // Squad matches are staked by design (a covenant match with no wager is refused), so the relay
        // under test has to be walked the way a player walks it.
        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1000, "invoice"));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var accept = await bob.Squad.AcceptAsync(open.MatchId);
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.StakeInvoice!.InvoiceId });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.MatchFeeInvoice!.InvoiceId });
        await alice.Squad.ResolveAsync(open.MatchId, new FightRequest("tl-squad-nonce"));

        var tl = await alice.Heroes.TimelineAsync(mine[0]);
        var relay = Find(tl, "squad");
        Assert.NotNull(relay);
        // /watch replays whole duels and death-matches; a "{squadId}:{slot}" receipt names neither, so
        // linking it would be a link to the "no replay" card.
        Assert.Null(relay!.WatchMatchId);
        Assert.DoesNotContain(tl.Events, e => e.WatchMatchId is not null && e.WatchMatchId.Contains(':'));
    }

    [Fact]
    public async Task AFusedHero_ShowsWhatWasBurnedForIt_AndMarksThemDestroyed()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-Fuser");
        var heroes = await alice.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        var (baseId, sacId) = (heroes[0].Id, heroes[1].Id);
        var burnedNames = new[] { store.Heroes[baseId].Name, store.Heroes[sacId].Name };

        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(baseId, sacId));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        var reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("tl-merge-nonce"));

        var tl = await alice.Heroes.TimelineAsync(reveal.Hero.Id);
        var forged = Find(tl, "fused");
        Assert.NotNull(forged);

        // BOTH inputs are named — this is the "show what was burned to make it" claim.
        Assert.Equal(
            new[] { baseId, sacId }.OrderBy(x => x, StringComparer.Ordinal),
            forged!.Related.Select(r => r.HeroId).OrderBy(x => x, StringComparer.Ordinal));
        // …and marked DESTROYED, which is the fact that must reach the page. This assertion used to demand
        // a NULL name, because a null name was the only way "gone" could be expressed and the merge erased
        // both rows. It is now the stronger claim: a headstone written at the burn site keeps the name, so
        // the page can say WHICH heroes died instead of printing two bare ids — and `Destroyed` carries
        // the "gone" that the null used to have to imply.
        Assert.All(forged.Related, r => Assert.True(r.Destroyed));
        Assert.Equal(
            burnedNames.OrderBy(x => x, StringComparer.Ordinal),
            forged.Related.Select(r => r.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("burned", forged.Summary);
    }

    [Fact]
    public async Task AGen0Founder_ReadsAsRecruited_WithNoInventedBirthMoment()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-Founder");
        var hero = (await alice.ClaimStartersAsync())[0];

        var tl = await alice.Heroes.TimelineAsync(hero.Id);
        var born = Assert.Single(tl.Events);
        Assert.Equal("born", born.Kind);
        Assert.Empty(born.Related);
        // Nothing in the arena stamps a hero with its birth moment, so there is no time to show. Zero is
        // the "unknown" sentinel the page renders as "moment not recorded" — never as the epoch.
        Assert.Equal(0, born.UnixSeconds);
    }

    [Fact]
    public async Task AnUnknownHero_Is404_NotAnEmptyTimeline()
    {
        var (alice, _) = await _factory.RegisterAsync("TL-Missing");
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Heroes.TimelineAsync("no-such-hero"));
    }

    // ── The one durable addition: what a hero sold for ─────────────────

    [Fact]
    public async Task ASale_RecordsThePrice_TheSeller_AndTheBuyer()
    {
        var (seller, sellerPlayer) = await _factory.RegisterAsync("TL-SaleSeller");
        var (buyer, buyerPlayer) = await _factory.RegisterAsync("TL-SaleBuyer");
        var hero = (await seller.ClaimStartersAsync())[0];

        const long ask = 25_000;
        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Offers.ClaimHeroAsync(offer.OfferId);

        var tl = await buyer.Heroes.TimelineAsync(hero.Id);
        var sold = Find(tl, "sold");
        Assert.NotNull(sold);
        Assert.Equal(ask, sold!.Sats);
        Assert.Contains(ask.ToString("N0"), sold.Summary);
        // Both counterparties, by name — the buyer is the half nothing recorded before.
        Assert.Contains("TL-SaleSeller", sold.Detail);
        Assert.Contains("TL-SaleBuyer", sold.Detail);

        var store = _factory.Services.GetRequiredService<GameStore>();
        var sale = store.HeroSales[offer.OfferId];
        Assert.Equal(sellerPlayer.PlayerId, sale.SellerId);
        Assert.Equal(buyerPlayer.PlayerId, sale.BuyerId);
        Assert.Equal(ask, sale.AskSats);
    }

    [Fact]
    public async Task ASale_IsRecordedOnce_HoweverManyTimesEitherProofRuns()
    {
        var (seller, _) = await _factory.RegisterAsync("TL-DupSeller");
        var (buyer, buyerPlayer) = await _factory.RegisterAsync("TL-DupBuyer");
        var hero = (await seller.ClaimStartersAsync())[0];

        const long ask = 31_000;
        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Offers.ClaimHeroAsync(offer.OfferId);

        // Drive the OTHER prover — reconcile, via any market read — repeatedly. It proves the same sale by
        // a different means (the covenant's treasury leg) and must not add a second one.
        for (var i = 0; i < 3; i++)
        {
            await buyer.Offers.ListAsync();
            await buyer.Offers.SoldAsync();
        }

        var store = _factory.Services.GetRequiredService<GameStore>();
        Assert.Single(store.HeroSales.Values, s => s.HeroId == hero.Id);
        // The buyer the claim learned is never overwritten by a prover that doesn't know one.
        Assert.Equal(buyerPlayer.PlayerId, store.HeroSales[offer.OfferId].BuyerId);

        var tl = await buyer.Heroes.TimelineAsync(hero.Id);
        Assert.Single(tl.Events, e => e.Kind == "sold");
    }

    [Fact]
    public async Task AReclaimedListing_IsNotRecordedAsASale()
    {
        var (seller, _) = await _factory.RegisterAsync("TL-Reclaimer");
        var hero = (await seller.ClaimStartersAsync())[0];

        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, 12_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();                                   // observed active
        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();                                   // observed closed

        // The listing closed, but the asset went back to its seller. `closed` alone cannot tell the two
        // apart, which is exactly why the sale is recorded on the chain's proof and not on the status.
        var store = _factory.Services.GetRequiredService<GameStore>();
        Assert.DoesNotContain(store.HeroSales.Values, s => s.HeroId == hero.Id);
        var tl = await seller.Heroes.TimelineAsync(hero.Id);
        Assert.DoesNotContain(tl.Events, e => e.Kind == "sold");
    }

    // ── Durability ─────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    [Fact]
    public async Task ASale_SurvivesARestart_AndTheTimelineAdmitsWhatDidNot()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-timeline-{Guid.NewGuid():N}.db");
        try
        {
            string heroId, offerId, buyerId;
            const long ask = 44_000;
            using (var first = HostOn(dbPath))
            {
                var (seller, _) = await first.RegisterAsync("TL-DurSeller");
                var (buyer, buyerPlayer) = await first.RegisterAsync("TL-DurBuyer");
                buyerId = buyerPlayer.PlayerId;
                var hero = (await seller.ClaimStartersAsync())[0];
                heroId = hero.Id;

                var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask));
                offerId = offer.OfferId;
                await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
                await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
                await buyer.Offers.ClaimHeroAsync(offer.OfferId);

                // A fight too, so the restart has something it CANNOT keep to be honest about.
                var second = (await buyer.RecruitAsync(1))[0];
                var duel = await buyer.Matches.OpenAsync(new OpenMatchRequest(hero.Id, second.Id));
                await buyer.Matches.FightAsync(duel.MatchId, new FightRequest("tl-dur-fight"));
                var before = await buyer.Heroes.TimelineAsync(hero.Id);
                Assert.Contains(before.Events, e => e.Kind == "spar");
                Assert.True(before.Complete);
                Assert.Null(before.Caveat);
            }

            using var restarted = HostOn(dbPath);
            var client = new ArkadeHeroesClient(restarted.CreateClient());

            var store = restarted.Services.GetRequiredService<GameStore>();
            var sale = store.HeroSales[offerId];
            Assert.Equal(ask, sale.AskSats);
            Assert.Equal(buyerId, sale.BuyerId);     // the counterparty survives with the price

            var after = await client.Heroes.TimelineAsync(heroId);
            var sold = Find(after, "sold");
            Assert.NotNull(sold);
            Assert.Equal(ask, sold!.Sats);

            // The receipt ledger is in memory and died with the last process, so the fight is gone. The
            // timeline must SAY the history is partial rather than present a life beginning at boot as a
            // whole one — the difference between an honest gap and a silent lie.
            Assert.DoesNotContain(after.Events, e => e.Kind == "spar");
            Assert.False(after.Complete);
            Assert.NotNull(after.Caveat);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ASale_ReplayedAfterARestart_DoesNotBecomeTwoSales()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-timeline-dup-{Guid.NewGuid():N}.db");
        try
        {
            string heroId, offerId;
            using (var first = HostOn(dbPath))
            {
                var (seller, _) = await first.RegisterAsync("TL-RedupSeller");
                var (buyer, _) = await first.RegisterAsync("TL-RedupBuyer");
                var hero = (await seller.ClaimStartersAsync())[0];
                heroId = hero.Id;
                var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, 9_000));
                offerId = offer.OfferId;
                await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
                await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
                await buyer.Offers.ClaimHeroAsync(offer.OfferId);
            }

            using var restarted = HostOn(dbPath);
            var client = new ArkadeHeroesClient(restarted.CreateClient());
            // Re-drive every market read the restarted process offers, so anything that re-proves the sale
            // gets its chance to record it a second time under the same key.
            for (var i = 0; i < 3; i++)
            {
                await client.Offers.ListAsync();
                await client.Offers.SoldAsync();
            }

            var store = restarted.Services.GetRequiredService<GameStore>();
            Assert.Single(store.HeroSales.Values, s => s.HeroId == heroId);
            Assert.True(store.HeroSales.ContainsKey(offerId));
            var tl = await client.Heroes.TimelineAsync(heroId);
            Assert.Single(tl.Events, e => e.Kind == "sold");
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
