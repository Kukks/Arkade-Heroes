using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The equip picker offers what the player OWNS, not what the shop sells.
///
/// <para>It was populated from <c>/api/items</c> — the whole catalogue — so almost every option in it was a
/// refusal waiting to happen: the server gates equip on <c>GetItemAssetBalanceAsync(player, item) &gt; 0</c>
/// and answers "You hold 0 unit(s) of X". That is the server being right about a question the page should
/// never have asked, and the cost lands on the player as an error message after the click.</para>
///
/// <para><c>/api/items/mine</c> — which <c>/gear</c> has used for its "owned ✓" markers all along — answers
/// this off the SAME predicate the equip gate uses, so the list and the rule agree by construction rather
/// than by a second copy that can drift.</para>
///
/// <para>What the picker still does NOT promise is that every option will be accepted: the level gate and
/// the already-allocated-units gate can refuse an item the player genuinely holds. "Owned" is the honest
/// claim, and it is the one the placeholder makes.</para>
/// </summary>
public class EquipPickerTests
{
    private const string HeroId = "hero-mine";

    private static ItemDto Owned => new(
        Id: "steel-saber", Name: "Steel Saber", Slot: "Weapon",
        MaxHp: 0, Attack: 5, Magic: 0, Defense: 0, Speed: 0, CritPercent: 0, PriceSats: 5_000);

    private static ItemDto NeverBought => new(
        Id: "gilded-aegis", Name: "Gilded Aegis", Slot: "Armor",
        MaxHp: 40, Attack: 0, Magic: 0, Defense: 9, Speed: 0, CritPercent: 0, PriceSats: 40_000);

    /// <summary>The hero page loads all-or-nothing, so every route it reads has to answer.</summary>
    private static PageTestContext HeroPage(Action<FakeApi> items, HeroDto? hero = null, bool gearCounters = false)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        var h = hero ?? Fixtures.Hero(HeroId, "Ashfang");
        var chain = gearCounters
            ? Fixtures.ChainInfo() with { Config = Fixtures.Config() with { GearCounters = true } }
            : Fixtures.ChainInfo();
        ctx.Api.GetFails($"/api/heroes/{HeroId}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{HeroId}", h);
        ctx.Api.Get($"/api/receipts/hero/{HeroId}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get("/api/chain/info", chain);
        ctx.Api.Get($"/api/heroes/{HeroId}/timeline", new HeroTimelineDto(HeroId, [], Complete: true, null));
        ctx.Api.Get("/api/bids", Array.Empty<BidDto>());
        items(ctx.Api);
        return ctx;
    }

    private static IRenderedComponent<HeroDetail> Render(PageTestContext ctx)
    {
        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("Equipment", cut.Markup));
        return cut;
    }

    /// <summary>The option labels currently in the picker (the placeholder excluded).</summary>
    private static List<string> Options(IRenderedComponent<HeroDetail> cut) =>
        cut.FindAll(".equip-add option")
            .Where(o => !string.IsNullOrEmpty(o.GetAttribute("value")))
            .Select(o => o.TextContent)
            .ToList();

    /// <summary>The defect itself: an item this player has never bought was on the menu.</summary>
    [Fact]
    public void ThePicker_DoesNotOfferAnItemThePlayerHasNeverBought()
    {
        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, NeverBought });
            api.Get("/api/items/mine", new Dictionary<string, long> { [Owned.Id] = 1 });
        });

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains(Options(cut), o => o.Contains(Owned.Name)));
        Assert.DoesNotContain(Options(cut), o => o.Contains(NeverBought.Name));
    }

    /// <summary>And it says WHICH list it is showing, so "your items" is a claim the page actually makes.</summary>
    [Fact]
    public void ThePicker_SaysItIsShowingTheItemsYouOwn()
    {
        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, NeverBought });
            api.Get("/api/items/mine", new Dictionary<string, long> { [Owned.Id] = 1 });
        });

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("one of your items", cut.Markup));
    }

    /// <summary>
    /// A player who owns nothing gets told where gear comes from, rather than a dropdown holding only its
    /// own placeholder — the empty state the old catalogue-wide list could never produce and so never had.
    /// </summary>
    [Fact]
    public void APlayerWhoOwnsNothing_IsPointedAtTheArmoryInsteadOfAnEmptyDropdown()
    {
        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, NeverBought });
            api.Get("/api/items/mine", new Dictionary<string, long>());
        });

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("don't own any gear yet", cut.Markup));
        Assert.Empty(cut.FindAll(".equip-add"));
    }

    /// <summary>
    /// A FAILED ownership read is not the same fact as owning nothing, and must not be rendered as either
    /// that or as a silent catalogue. It falls back to the full list — refusing to equip at all over a
    /// failed read would be worse — and says so, which is the same call /gear makes about its "owned ✓"
    /// markers when the identical read fails.
    /// </summary>
    [Fact]
    public void AFailedOwnershipRead_SaysSoRatherThanPassingTheCatalogueOffAsYourInventory()
    {
        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, NeverBought });
            api.GetFails("/api/items/mine");
        });

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("Couldn't read which items you own", cut.Markup));
        Assert.Contains(Options(cut), o => o.Contains(NeverBought.Name));
        Assert.DoesNotContain("one of your items", cut.Markup);
    }

    /// <summary>
    /// A cold deep-link to your own hero still ends up with your items.
    ///
    /// <para>The shell resumes sign-in AFTER a page has initialised on a cold WASM boot — the startup ORDER
    /// problem <c>/heroes?mine=1</c> already documents. At load time the hero is therefore not yet "mine",
    /// and the ownership read is correctly skipped; without a second chance when sign-in lands, the owner is
    /// left looking at the whole catalogue under a line claiming their inventory could not be read, when it
    /// had simply never been asked for. Two untrue statements at once, which is what this whole change is
    /// about not doing.</para>
    /// </summary>
    [Fact]
    public void SignInLandingAfterTheFirstRender_StillGetsYourItemsRatherThanAFalseReadFailure()
    {
        var ctx = new PageTestContext();   // NOT signed in yet — the cold-boot order
        ctx.Api.GetFails($"/api/heroes/{HeroId}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{HeroId}", Fixtures.Hero(HeroId, "Ashfang"));
        ctx.Api.Get($"/api/receipts/hero/{HeroId}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        ctx.Api.Get($"/api/heroes/{HeroId}/timeline", new HeroTimelineDto(HeroId, [], Complete: true, null));
        ctx.Api.Get("/api/bids", Array.Empty<BidDto>());
        ctx.Api.Get("/api/items", new[] { Owned, NeverBought });
        ctx.Api.Get("/api/items/mine", new Dictionary<string, long> { [Owned.Id] = 1 });

        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("Equipment", cut.Markup));
        Assert.DoesNotContain("GET /api/items/mine", ctx.Api.Requested);   // correctly not asked yet

        ctx.SignIn();   // the shell's login lands

        cut.WaitForAssertion(() => Assert.Contains("one of your items", cut.Markup));
        Assert.DoesNotContain("Couldn't read which items you own", cut.Markup);
        Assert.DoesNotContain(Options(cut), o => o.Contains(NeverBought.Name));
        ctx.Dispose();
    }

    /// <summary>
    /// Someone else's hero must not trigger the ownership read at all.
    ///
    /// <para>Not a tidiness point. <c>/api/items/mine</c> is AUTHENTICATED, so asking about a hero you do
    /// not own — and, on a cold load, asking before sign-in resumes — answers 400 and writes a
    /// failed-resource line into the browser console on a page that renders no picker anyway. The browser
    /// suite asserts zero console output for all 27 routes and failed on exactly this while it was being
    /// written; this is the same fact, asserted where it costs a second rather than a publish.</para>
    /// </summary>
    [Fact]
    public void SomeoneElsesHero_NeverAsksWhatYouOwn()
    {
        var theirs = Fixtures.Hero(HeroId, "Direbloom", ownerId: "player-2");

        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, NeverBought });
            api.Get("/api/items/mine", new Dictionary<string, long> { [Owned.Id] = 1 });
        }, theirs);

        var cut = Render(ctx);

        cut.WaitForAssertion(() => Assert.Contains("Direbloom", cut.Markup));
        Assert.DoesNotContain("GET /api/items/mine", ctx.Api.Requested);
        Assert.Empty(cut.FindAll(".equip-add"));
    }

    /// <summary>
    /// The trap this fix had to avoid, and the reason the owned ids live in their own set instead of being
    /// filtered into the catalogue.
    ///
    /// <para><c>CounterGear</c> resolves this hero's EQUIPPED item ids against <c>_catalog</c> to draw the
    /// build-shape counter chips. Narrowing the catalogue to "what you own" — the obvious one-line fix —
    /// silently drops any worn item that is not in the owned list, and the card then prints "no counter
    /// charm · matchup-neutral" about a hero visibly wearing one. That is a WRONG statement about combat,
    /// not a missing chip, which is what makes it worth a test of its own.</para>
    ///
    /// <para>Needs <c>GearCounters</c> ON: the whole shape block is behind that flag, and with it off this
    /// test would render none of the markup it is about and pass for no reason. Turned on per-test because
    /// <see cref="Fixtures.Config"/> leaves it off — note that this is the FIXTURE's default, not the
    /// shipped one: <c>GameOptions.GearCounters</c> (and <c>InnateAbilities</c>) default to true, so a real
    /// server publishes both ON and every other render here is of a config production never serves.</para>
    /// </summary>
    [Fact]
    public void NarrowingTheCatalogue_WouldLieAboutGearTheHeroIsWearing()
    {
        var counterCharm = NeverBought with { Counters = "Offense" };
        var wearing = Fixtures.Hero(HeroId, "Ashfang") with
        {
            Equipment = new Dictionary<string, string> { ["Armor"] = counterCharm.Id },
            // This used to override GenomeHex with a hand-written 32-byte value, because the shared fixture's
            // was 16 bytes — which Genome.FromHex rejects, and the page swallows into "no shape", so the whole
            // block this test is about would never render and the test would pass without asserting anything.
            // The shared fixture now carries a real genome and FixtureGenomeTests keeps it that way, so the
            // override is gone and this test exercises the same hero every other test does.
        };

        using var ctx = HeroPage(api =>
        {
            api.Get("/api/items", new[] { Owned, counterCharm });
            // The worn charm is deliberately ABSENT from the owned list: its unit is allocated to this very
            // hero, which is exactly the state in which a catalogue-narrowing fix loses it.
            api.Get("/api/items/mine", new Dictionary<string, long> { [Owned.Id] = 1 });
        }, wearing, gearCounters: true);

        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("build shape", cut.Markup));

        Assert.Contains("counters", cut.Markup);
        Assert.DoesNotContain("matchup-neutral", cut.Markup);
    }
}
