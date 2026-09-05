using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A staked picker has to say how the fight reads BEFORE the player pays for it. Measured over 6,000
/// real <c>BattleEngine</c> fights: drawn at RANDOM, 45% of pairings sit above a 50% power gap and the
/// favourite takes 99.9% of those in ~3 turns; drawn as <c>/api/matchmaking</c> suggests, the mean gap
/// is 7.5% and the favourite wins 64% over ~6.6 turns. All three staked screens are pinned here —
/// /duel and /deathmatch already read the endpoint, /squad did not.
/// </summary>
public class OpponentGuidanceTests
{
    private const string Bruisers = "player-2";
    private const string Peers = "player-3";

    private static readonly HeroDto[] Mine =
    [
        Fixtures.Hero("mine-1", "Ashfang"),
        Fixtures.Hero("mine-2", "Bramblecoat"),
        Fixtures.Hero("mine-3", "Cinderwake"),
    ];

    /// <summary>A rout at every slot — and owner id "player-2", so it leads the list until the guidance
    /// re-ranks it. The ordering test below is only worth anything because of that.</summary>
    private static readonly HeroDto[] BruiserSquad =
    [
        Fixtures.Hero("bruiser-1", "Gravemaw", Bruisers, level: 12),
        Fixtures.Hero("bruiser-2", "Hollowtide", Bruisers, level: 12),
        Fixtures.Hero("bruiser-3", "Ironvein", Bruisers, level: 12),
    ];

    private static readonly HeroDto[] PeerSquad =
    [
        Fixtures.Hero("peer-1", "Direbloom", Peers),
        Fixtures.Hero("peer-2", "Emberkin", Peers),
        Fixtures.Hero("peer-3", "Frostmane", Peers),
    ];

    private static OpponentSuggestionDto Suggest(
        HeroDto opponent, int powerScore, int gapPercent, string favor,
        long xpIfWin = 12, long xpIfLose = 8) =>
        new(opponent, opponent.OwnerId, LevelGap: Math.Abs(3 - opponent.Level),
            XpIfYouWin: xpIfWin, XpIfYouLose: xpIfLose,
            PowerScore: powerScore, PowerGapPercent: gapPercent, Favor: favor);

    private static string Chip(IElement card) => card.QuerySelector(".chip")?.TextContent.Trim() ?? "";

    // ── /squad — the screen that had none ────────────────────────────────────────────────────────────

    /// <param name="perSlot">The server's reads for slot 1, 2 and 3 of my lineup, in order.</param>
    private static PageTestContext Arena(params OpponentSuggestionDto[][] perSlot)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", Mine);
        ctx.Api.Get<HeroDto[]>("/api/heroes", [.. Mine, .. BruiserSquad, .. PeerSquad]);
        ctx.Api.Get("/api/squad", Array.Empty<SquadMatchDto>());
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        for (var i = 0; i < perSlot.Length; i++)
            ctx.Api.Get($"/api/matchmaking/{Mine[i].Id}", perSlot[i]);
        return ctx;
    }

    private static OpponentSuggestionDto[][] BothSquadsScored() =>
    [
        [Suggest(BruiserSquad[0], 940, 62, "underdog"), Suggest(PeerSquad[0], 305, 4, "even")],
        [Suggest(BruiserSquad[1], 950, 63, "underdog"), Suggest(PeerSquad[1], 310, 5, "even")],
        [Suggest(BruiserSquad[2], 960, 64, "underdog"), Suggest(PeerSquad[2], 315, 6, "even")],
    ];

    private static OpponentSuggestionDto[][] OnlyPeersScored(
        string s1 = "even", string s2 = "even", string s3 = "even") =>
    [
        [Suggest(PeerSquad[0], 305, 4, s1, xpIfWin: 40, xpIfLose: 25)],
        [Suggest(PeerSquad[1], 310, 5, s2, xpIfWin: 41, xpIfLose: 26)],
        [Suggest(PeerSquad[2], 315, 6, s3, xpIfWin: 42, xpIfLose: 27)],
    ];

    private static IReadOnlyList<IElement> Cards(IRenderedComponent<Squad> cut) =>
        cut.FindAll("button.oppcard");

    private static IElement Card(IRenderedComponent<Squad> cut, string heroName) =>
        Cards(cut).Single(c => c.TextContent.Contains(heroName));

    private static void FieldLineup(IRenderedComponent<Squad> cut)
    {
        // Re-queried between picks: a handle taken before the re-render holds a dead event handler.
        for (var i = 0; i < 3; i++) cut.FindAll(".lineup-picks select")[i].Change(Mine[i].Id);
    }

    private static IRenderedComponent<Squad> Fielded(PageTestContext ctx)
    {
        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.Contains("Field a squad", cut.Markup));
        FieldLineup(cut);
        return cut;
    }

    /// <summary>Three positional duels, so all three get a read — not one hero standing in for the rest.</summary>
    [Fact]
    public void Squad_EveryPositionalPairingCarriesItsPowerAndFavour()
    {
        using var ctx = Arena(BothSquadsScored());
        var cut = Fielded(ctx);

        cut.WaitForAssertion(() => Assert.Contains("pow", Card(cut, "Direbloom").TextContent));

        var peers = Card(cut, "Direbloom").TextContent;
        Assert.Contains("pow 305", peers);
        Assert.Contains("Δ4%", peers);
        Assert.Contains("pow 310", peers);
        Assert.Contains("Δ5%", peers);
        Assert.Contains("pow 315", peers);
        Assert.Contains("Δ6%", peers);
    }

    /// <summary>The 45%-of-random-pairings case, said out loud while the wager is still yours.</summary>
    [Fact]
    public void Squad_ASquadThatWouldRoutYouSaysSoBeforeTheWagerIsStaked()
    {
        using var ctx = Arena(BothSquadsScored());
        var cut = Fielded(ctx);

        cut.WaitForAssertion(() => Assert.Contains("pow", Card(cut, "Gravemaw").TextContent));

        var bruisers = Card(cut, "Gravemaw");
        Assert.Equal("underdog", Chip(bruisers));
        Assert.Contains("Δ62%", bruisers.TextContent);
    }

    [Fact]
    public void Squad_TheClosestMatchedSquadIsOfferedFirst()
    {
        using var ctx = Arena(BothSquadsScored());

        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.Contains("Field a squad", cut.Markup));

        // Unscored the rout leads, so the flip below is the guidance and not the fixture order.
        Assert.Contains("Gravemaw", Cards(cut)[0].TextContent);
        Assert.DoesNotContain("closest match first", cut.Markup);

        FieldLineup(cut);

        cut.WaitForAssertion(() => Assert.Contains("Direbloom", Cards(cut)[0].TextContent));
        Assert.Contains("closest match first", cut.Markup);
    }

    /// <summary>Two slots take the pot, so two slots decide how the card reads.</summary>
    [Theory]
    [InlineData("favored", "favored", "even", "favored")]
    [InlineData("underdog", "even", "underdog", "underdog")]
    [InlineData("favored", "even", "underdog", "even")]
    [InlineData("even", "even", "even", "even")]
    public void Squad_TheBestOfThreeLeansWhereTwoOfItsSlotsDo(
        string slot1, string slot2, string slot3, string expected)
    {
        using var ctx = Arena(OnlyPeersScored(slot1, slot2, slot3));
        var cut = Fielded(ctx);

        cut.WaitForAssertion(() => Assert.NotEqual("", Chip(Card(cut, "Direbloom"))));
        Assert.Equal(expected, Chip(Card(cut, "Direbloom")));
    }

    /// <summary>A staked squad match settles a conserved XP transfer per SLOT (GameService.ResolveSquad),
    /// so the swing is the sum of the three — and the page charged it without ever quoting it.</summary>
    [Fact]
    public void Squad_TheXpSwingAcrossAllThreePositionsIsQuotedBeforeTheMatchIsOpened()
    {
        using var ctx = Arena(OnlyPeersScored());
        var cut = Fielded(ctx);

        cut.WaitForAssertion(() => Assert.NotNull(Card(cut, "Direbloom").QuerySelector(".oppcard-xp")));

        var xp = Card(cut, "Direbloom").QuerySelector(".oppcard-xp")!.TextContent;
        Assert.Contains("+123", xp);
        Assert.Contains("-78", xp);
    }

    /// <summary>Matchmaking returns a capped page, so some squad on screen will always be unscored. That
    /// one gets names and levels and nothing else — an invented favourability is worse than none.</summary>
    [Fact]
    public void Squad_ASquadTheServerDidNotScoreIsLeftUnannotatedRatherThanGuessed()
    {
        using var ctx = Arena(OnlyPeersScored());
        var cut = Fielded(ctx);

        cut.WaitForAssertion(() => Assert.Contains("pow", Card(cut, "Direbloom").TextContent));

        var unscored = Card(cut, "Gravemaw");
        Assert.Contains("Gravemaw L12", unscored.TextContent);
        Assert.DoesNotContain("pow", unscored.TextContent);
        Assert.Equal("", Chip(unscored));
        Assert.Null(unscored.QuerySelector(".oppcard-xp"));
    }

    [Fact]
    public void Squad_AFailedMatchmakingReadStillLetsYouFieldASquad()
    {
        using var ctx = Arena();
        foreach (var h in Mine) ctx.Api.GetFails($"/api/matchmaking/{h.Id}");

        var cut = Fielded(ctx);
        Cards(cut)[0].Click();

        var challenge = cut.FindAll("button").First(b => b.TextContent.Contains("Challenge squad"));
        Assert.False(challenge.HasAttribute("disabled"));
        Assert.DoesNotContain("pow", Cards(cut)[0].TextContent);
    }

    // ── /duel and /deathmatch — the two that already had it ──────────────────────────────────────────

    private static PageTestContext DuelArena(params OpponentSuggestionDto[] reads)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { Mine[0] });
        ctx.Api.Get("/api/matches", Array.Empty<MatchDto>());
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        ctx.Api.Get($"/api/matchmaking/{Mine[0].Id}", reads);
        return ctx;
    }

    private static IElement PickedOpponentCard<TPage>(IRenderedComponent<TPage> cut, string opponentName)
        where TPage : IComponent
    {
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        cut.FindAll("select")[0].Change(Mine[0].Id);
        cut.WaitForAssertion(() => Assert.Contains(opponentName, cut.Markup));
        return cut.Find("button.oppcard");
    }

    [Fact]
    public void Duel_AnOpponentCardCarriesPowerFavourAndBothSidesOfTheXpSwing()
    {
        using var ctx = DuelArena(Suggest(PeerSquad[0], 305, 4, "even", xpIfWin: 40, xpIfLose: 25));

        var card = PickedOpponentCard(ctx.Render<Duel>(), "Direbloom");

        Assert.Contains("pow 305", card.TextContent);
        Assert.Contains("Δ4%", card.TextContent);
        Assert.Equal("even", Chip(card));
        Assert.Contains("+40", card.TextContent);
        Assert.Contains("-25", card.TextContent);
    }

    [Fact]
    public void Duel_AnUnderdogWithNothingToLoseIsBadgedAFreeShot()
    {
        using var ctx = DuelArena(Suggest(BruiserSquad[0], 940, 62, "underdog", xpIfWin: 90, xpIfLose: 0));

        var card = PickedOpponentCard(ctx.Render<Duel>(), "Gravemaw");

        Assert.Equal("free shot", Chip(card));
    }

    /// <summary>Favor calls a hero "underdog" three levels down while XpIfYouLose only reaches zero at
    /// four, so the badge has to test both — the word alone would promise a free shot that costs XP.</summary>
    [Fact]
    public void Duel_AnUnderdogThatCanStillLoseXpIsNotBadgedAFreeShot()
    {
        using var ctx = DuelArena(Suggest(BruiserSquad[0], 940, 62, "underdog", xpIfWin: 90, xpIfLose: 7));

        var card = PickedOpponentCard(ctx.Render<Duel>(), "Gravemaw");

        Assert.Equal("underdog", Chip(card));
    }

    /// <summary>Roughly 80% of staked duels in a young arena, measured across three playthrough seeds.</summary>
    [Fact]
    public void Duel_AFightThatCanMoveNoXpSaysSo()
    {
        using var ctx = DuelArena(Suggest(PeerSquad[0], 300, 2, "even", xpIfWin: 0, xpIfLose: 0));

        var card = PickedOpponentCard(ctx.Render<Duel>(), "Direbloom");

        Assert.Contains("no xp at stake", card.TextContent);
    }

    [Fact]
    public void Duel_AFightWithXpToWinIsNotBadgedThatWay()
    {
        using var ctx = DuelArena(Suggest(PeerSquad[0], 300, 2, "even", xpIfWin: 40, xpIfLose: 0));

        var card = PickedOpponentCard(ctx.Render<Duel>(), "Direbloom");

        Assert.DoesNotContain("no xp at stake", card.TextContent);
    }

    [Fact]
    public void DeathMatch_AnOpponentCardCarriesPowerAndFavourBeforeAHeroIsStakedToTheDeath()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { Mine[0] });
        ctx.Api.Get("/api/deathmatch", Array.Empty<DeathMatchDto>());
        ctx.Api.Get($"/api/matchmaking/{Mine[0].Id}",
            new[] { Suggest(BruiserSquad[0], 940, 62, "underdog") });

        var card = PickedOpponentCard(ctx.Render<DeathMatch>(), "Gravemaw");

        Assert.Contains("pow 940", card.TextContent);
        Assert.Contains("Δ62%", card.TextContent);
        Assert.Equal("underdog", Chip(card));
    }
}
