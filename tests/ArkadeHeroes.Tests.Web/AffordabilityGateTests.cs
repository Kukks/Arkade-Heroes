using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A spend the wallet cannot cover is answered BEFORE it is started, not after.
///
/// <para><c>/gauntlet</c> and <c>/heroes</c> have done this since they learned to quote their own prices:
/// the button greys out and a line underneath names the balance, names the cost, and links to the top-up.
/// <c>/gear</c>, <c>/squad</c> and <c>/tournaments</c> did not — they took the click, walked the player
/// through invoice preparation, and surfaced the server's rejection as an error message.</para>
///
/// <para>Both halves are asserted for every page, because a gate is two claims and each fails differently:
/// a missing gate lets a doomed spend start, and a stuck one silently walls a player out of a game they can
/// afford. The "why" is asserted alongside the disabling — a dead control with no explanation is its own
/// defect, and matching the wording of the two pages that already got this right is the point.</para>
/// </summary>
public class AffordabilityGateTests
{
    private const string Me = "player-1";
    private const string MyHero = "hero-mine";
    private const string Rival = "player-2";

    /// <summary>The line the two established pages print. Its presence is the "why".</summary>
    private const string TopUp = "top up";

    private static HeroDto Mine => Fixtures.Hero(MyHero, "Ashfang");

    private static ItemDto Sabre => new(
        Id: "steel-saber", Name: "Steel Saber", Slot: "Weapon",
        MaxHp: 0, Attack: 5, Magic: 0, Defense: 0, Speed: 0, CritPercent: 0,
        PriceSats: 5_000);

    private static bool Disabled(IElement e) => e.HasAttribute("disabled");

    // ── /gear ────────────────────────────────────────────────────────────────────────────────────────

    private static PageTestContext Armory(long balanceSats)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: balanceSats);
        ctx.Api.Get("/api/items", new[] { Sabre });
        ctx.Api.Get("/api/items/mine", Array.Empty<string>());
        return ctx;
    }

    [Fact]
    public void Gear_APlayerWhoCannotAffordAnItem_IsToldWhyRatherThanSoldIt()
    {
        using var ctx = Armory(balanceSats: 100);

        var cut = ctx.Render<Gear>();
        cut.WaitForAssertion(() => Assert.Contains("Steel Saber", cut.Markup));

        var buy = cut.FindAll("button").First(b => b.TextContent.Contains("Buy"));
        Assert.True(Disabled(buy), "an item 4,900 sats out of reach must not offer a live Buy button");
        Assert.Contains(TopUp, cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("short", cut.Markup);
    }

    [Fact]
    public void Gear_APlayerWhoCanAffordAnItem_IsNotBlocked()
    {
        using var ctx = Armory(balanceSats: 9_000);

        var cut = ctx.Render<Gear>();
        cut.WaitForAssertion(() => Assert.Contains("Steel Saber", cut.Markup));

        var buy = cut.FindAll("button").First(b => b.TextContent.Contains("Buy"));
        Assert.False(Disabled(buy), "a funded player must still be able to buy");
        Assert.DoesNotContain("short", cut.Markup);
    }

    // ── /tournaments ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Someone else's open bracket, joinable — the buy-in leaves MY wallet the moment I join.</summary>
    private static TournamentDto OpenBracket(long buyInSats) =>
        new("t-1", OpenerPlayerId: Rival, BuyInSats: buyInSats, Size: 4, Joined: 1, "open",
            Entrants: [new TournamentEntrantDto(Rival, "hero-rival")],
            ChampionHeroId: null, ChampionPrizeSats: 0, EntrantsCommitmentHex: null);

    private static PageTestContext Brackets(long balanceSats, long buyInSats)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: balanceSats);
        ctx.Api.Get("/api/heroes/mine", new[] { Mine });
        ctx.Api.Get("/api/heroes", new[] { Mine, Fixtures.Hero("hero-rival", "Direbloom", ownerId: Rival) });
        ctx.Api.Get("/api/tournament", new[] { OpenBracket(buyInSats) });
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    /// <summary>Picks a hero, so the Join button's OTHER disabling condition is satisfied and the assertion
    /// below is about affordability rather than about an empty dropdown.</summary>
    private static IRenderedComponent<Tournaments> BracketsWithHeroPicked(PageTestContext ctx)
    {
        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));
        cut.FindAll("select").First().Change(MyHero);
        return cut;
    }

    [Fact]
    public void Tournaments_APlayerWhoCannotCoverTheBuyIn_IsToldWhyRatherThanLetIn()
    {
        using var ctx = Brackets(balanceSats: 100, buyInSats: 2_000);

        var cut = BracketsWithHeroPicked(ctx);

        var join = cut.FindAll("button").First(b => b.TextContent.Contains("Join"));
        Assert.True(Disabled(join), "a 2,000 sat buy-in must not be joinable on 100 sats");
        Assert.Contains(TopUp, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tournaments_APlayerWhoCanCoverTheBuyIn_IsNotBlocked()
    {
        using var ctx = Brackets(balanceSats: 9_000, buyInSats: 2_000);

        var cut = BracketsWithHeroPicked(ctx);

        var join = cut.FindAll("button").First(b => b.TextContent.Contains("Join"));
        Assert.False(Disabled(join), "a funded entrant must still be able to join");
    }

    /// <summary>Hosting pays the host's OWN buy-in, so it is gated too — a separate control from Join, and
    /// one whose cost the player types rather than reads off a bracket.</summary>
    [Fact]
    public void Tournaments_HostingABracketYouCannotPayFor_IsRefusedUpFront()
    {
        using var ctx = Brackets(balanceSats: 100, buyInSats: 2_000);

        var cut = BracketsWithHeroPicked(ctx);

        // The host form defaults to a 1,000 sat buy-in, which 100 sats cannot cover.
        var host = cut.FindAll("button").First(b => b.TextContent.Contains("Host bracket"));
        Assert.True(Disabled(host), "hosting stakes the host's own buy-in and must be gated like joining");
        Assert.Contains("hosting stakes", cut.Markup);
    }

    // ── /squad ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A rival's challenge waiting on MY squad: accepting stakes the wager (and a match fee on top).</summary>
    private static SquadMatchDto Challenge(long wagerSats) => new(
        MatchId: "s-1",
        ChallengerLineup: ["hero-r1", "hero-r2", "hero-r3"],
        DefenderLineup: [MyHero, "hero-mine-2", "hero-mine-3"],
        WagerSats: wagerSats, Status: "open", Result: null);

    private static PageTestContext Squads(long balanceSats, long wagerSats)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: balanceSats);
        HeroDto[] mine =
        [
            Mine,
            Fixtures.Hero("hero-mine-2", "Bramblecoat"),
            Fixtures.Hero("hero-mine-3", "Cinderwake"),
        ];
        HeroDto[] theirs =
        [
            Fixtures.Hero("hero-r1", "Direbloom", ownerId: Rival),
            Fixtures.Hero("hero-r2", "Emberkin", ownerId: Rival),
            Fixtures.Hero("hero-r3", "Frostmane", ownerId: Rival),
        ];
        ctx.Api.Get("/api/heroes/mine", mine);
        ctx.Api.Get<HeroDto[]>("/api/heroes", [.. mine, .. theirs]);
        ctx.Api.Get("/api/squad", new[] { Challenge(wagerSats) });
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    [Fact]
    public void Squad_APlayerWhoCannotCoverTheWager_CannotAcceptTheChallenge()
    {
        using var ctx = Squads(balanceSats: 100, wagerSats: 3_000);

        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.Contains("Accept", cut.Markup));

        var accept = cut.FindAll("button").First(b => b.TextContent.Contains("Accept"));
        Assert.True(Disabled(accept), "a 3,000 sat stake must not be accepted on 100 sats");
        Assert.Contains(TopUp, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Squad_APlayerWhoCanCoverTheWager_CanStillAccept()
    {
        using var ctx = Squads(balanceSats: 9_000, wagerSats: 3_000);

        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.Contains("Accept", cut.Markup));

        var accept = cut.FindAll("button").First(b => b.TextContent.Contains("Accept"));
        Assert.False(Disabled(accept), "a funded defender must still be able to accept");
    }

    /// <summary>
    /// Opening a challenge is the page's other spend, and the only one whose cost is the WAGER PLUS the
    /// per-side match fee — a total the page already computed and displayed while still letting an empty
    /// wallet commit to it. Driven through the real form because the total depends on the lineup's top level.
    /// </summary>
    [Fact]
    public void Squad_OpeningAChallengeYouCannotStake_IsRefusedUpFront()
    {
        using var ctx = Squads(balanceSats: 100, wagerSats: 3_000);

        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.Contains("Field a squad", cut.Markup));

        // Re-queried between each change: every pick re-renders the form, and an element handle taken before
        // that render points at an event handler the new tree no longer has.
        cut.FindAll(".lineup-picks select")[0].Change(MyHero);
        cut.FindAll(".lineup-picks select")[1].Change("hero-mine-2");
        cut.FindAll(".lineup-picks select")[2].Change("hero-mine-3");
        cut.FindAll("button.oppcard").First().Click();

        var challenge = cut.FindAll("button").First(b => b.TextContent.Contains("Challenge squad"));
        Assert.True(Disabled(challenge), "the default 1,000 sat wager plus a match fee is beyond 100 sats");
        Assert.Contains("in total", cut.Markup);
        Assert.Contains(TopUp, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
