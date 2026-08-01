using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// One defect, twice more: an outcome drawn from the ACTION RESPONSE instead of from the list every
/// player can read.
///
/// <para><see cref="MatchVisibilityTests"/> pinned that a match must APPEAR for both sides. This pins the
/// next thing along — that when it is over, both sides can see WHAT HAPPENED. The two are separate bugs
/// with the same shape: the page holds a field populated only by the player who pressed the button, and
/// everything worth knowing renders inside <c>@if (thatField is not null)</c>. The other player's copy of
/// the row is therefore permanently outcome-free, however many times they reload — and on /tournaments
/// they can even press Verify, have it run to completion, and watch nothing at all appear.</para>
///
/// <para>Neither needs a byte of new server data. <c>GET /api/tournament</c> hands every reader the
/// champion and the champion's prize; <c>GET /api/squad</c> hands them the whole
/// <see cref="SquadResultDto"/>, duels included. The fix is which object the markup reads.</para>
///
/// <para>The precedent both follow is <c>Duel.razor</c>'s History row, which already answers "did I win?"
/// from the LIST DTO via a <c>bool? IWon(m)</c> helper. Each page here grows the same helper against its
/// own shape.</para>
/// </summary>
public class OutcomeVisibilityTests
{
    private const string Me = "player-1";
    private const string Rival = "player-2";

    // ── Tournaments: the champion who did not press "Run bracket" ────────────

    private const string MyHero = "hero-mine";
    private const string RivalHero = "hero-rival";

    private static HeroDto Mine => Fixtures.Hero(MyHero, "Ashfang");
    private static HeroDto Theirs => Fixtures.Hero(RivalHero, "Direbloom", ownerId: Rival);

    /// <summary>A two-hero bracket I paid into, in whatever state the server reports.</summary>
    private static TournamentDto Bracket(string status, string? championHeroId, long championPrizeSats = 0) =>
        new("t-1", OpenerPlayerId: Rival, BuyInSats: 1_000, Size: 2, Joined: 2, status,
            Entrants: new[] { new TournamentEntrantDto(Rival, RivalHero), new TournamentEntrantDto(Me, MyHero) },
            ChampionHeroId: championHeroId, ChampionPrizeSats: championPrizeSats,
            EntrantsCommitmentHex: "aabb");

    private static PageTestContext SignedTournaments(params TournamentDto[] brackets)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Mine });
        ctx.Api.Get("/api/heroes", new[] { Mine, Theirs });
        ctx.Api.Get("/api/tournament", brackets);
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    /// <summary>
    /// The champion never ran the bracket — somebody else in it pressed the button — so the page must
    /// tell them, unprompted, that they won and what the pot paid them.
    ///
    /// <para>The prize is the load-bearing assert: "champion Ashfang" was already on the row, so a player
    /// could read their own hero's name there and still have no idea whether that meant anything or what
    /// it was worth. <c>ChampionPrizeSats</c> was on the DTO the whole time and nothing read it.</para>
    /// </summary>
    [Fact]
    public void Tournament_TheChampionSeesThatTheyWonAndWhatItPaid()
    {
        using var ctx = SignedTournaments(Bracket("resolved", MyHero, championPrizeSats: 7_000));

        var cut = ctx.Render<Tournaments>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));
        Assert.Contains("you won", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(7_000L.ToString("N0"), cut.Markup);
    }

    /// <summary>
    /// The other half of the same predicate, so the fix cannot be "always say you won". An entrant whose
    /// hero is not the champion is told they were knocked out, and is never told they won.
    /// </summary>
    [Fact]
    public void Tournament_AKnockedOutEntrantIsNotToldTheyWon()
    {
        using var ctx = SignedTournaments(Bracket("resolved", RivalHero, championPrizeSats: 7_000));

        var cut = ctx.Render<Tournaments>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));
        Assert.DoesNotContain("you won", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The silent failure, exactly: pressing Verify on a concluded bracket RAN — the recompute completed
    /// and produced a verdict — and then had nowhere to draw, because the badge lived inside the results
    /// block and the results block was gated on the resolve RESPONSE. Zero console errors, zero pixels.
    ///
    /// <para>The verdict here is a FAILING one (a synthetic replay cannot match a real commitment), and
    /// that is deliberate: what is under test is that a verdict renders at all, and the failing branch is
    /// the one a player must never be denied. Both branches share the badge.</para>
    /// </summary>
    [Fact]
    public void Tournament_VerifyingAConcludedBracketRendersAVerdict()
    {
        var bracket = Bracket("resolved", MyHero, championPrizeSats: 7_000);
        using var ctx = SignedTournaments(bracket);
        ctx.Api.Get("/api/tournament/t-1", bracket);
        ctx.Api.Get("/api/tournament/t-1/replay", new TournamentReplayDto(
            Entrants: new[] { Mine, Theirs },
            Bracket: new[] { new TournamentMatchDto(0, 0, RivalHero, MyHero, MyHero) },
            ChampionHeroId: MyHero,
            CommitmentHex: "00", ServerSeedHex: "00", EntropyHex: "00", Nonce: "nonce-1",
            EntrantsCommitmentHex: "aabb"));

        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));

        var verify = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == "Verify");
        Assert.NotNull(verify);
        verify.Click();

        cut.WaitForAssertion(() => Assert.Contains("Verification FAILED", cut.Markup));
    }

    /// <summary>
    /// The same click when the server can no longer produce the bracket — a reachable state, not a
    /// hypothetical: <c>PersistedTournament</c> deliberately stores neither <c>Result</c> nor <c>Prizes</c>,
    /// so after a restart an already-resolved bracket answers <c>/replay</c> with a 404 and reports no
    /// champion at all. Verification then cannot complete either.
    ///
    /// <para>The rule is that a Verify click always produces something. It must not silently do nothing —
    /// which is the original defect — and it must not print an empty results card that reads as "this
    /// bracket had no matches in it". It says both true things: the rounds are unreadable, and why the
    /// recompute could not be run.</para>
    /// </summary>
    [Fact]
    public void Tournament_VerifyingStillAnswersWhenTheBracketCannotBeFetched()
    {
        // No stub for /api/tournament/t-1 or its replay: FakeApi 404s anything unregistered, which is
        // exactly what a restarted server does with a bracket whose result it no longer holds.
        using var ctx = SignedTournaments(Bracket("resolved", MyHero, championPrizeSats: 7_000));

        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Verify").Click();

        cut.WaitForAssertion(() => Assert.Contains("Verification FAILED", cut.Markup));
        Assert.Contains("round-by-round bracket couldn't be read", cut.Markup);
    }

    // ── Squad 3v3: the side that did not press "Fight!" ──────────────────────

    private static readonly string[] MyLineup = ["mine-1", "mine-2", "mine-3"];
    private static readonly string[] RivalLineup = ["rival-1", "rival-2", "rival-3"];

    private static HeroDto[] MyHeroes =>
        MyLineup.Select((id, i) => Fixtures.Hero(id, $"Mine{i + 1}")).ToArray();

    private static HeroDto[] RivalHeroes =>
        RivalLineup.Select((id, i) => Fixtures.Hero(id, $"Rival{i + 1}", ownerId: Rival)).ToArray();

    private static BattleResultDto Fight(string winnerId, string loserId) =>
        new(winnerId, loserId, Turns: 1, Events: [], WinnerRemainingHp: 50, WinnerMaxHp: 100);

    /// <summary>
    /// A resolved 3v3 as the SERVER reports it to both sides. <paramref name="iAmChallenger"/> picks which
    /// lineup is mine; <paramref name="challengerWon"/> picks who took the pot.
    /// </summary>
    private static SquadMatchDto ResolvedSquad(bool iAmChallenger, bool challengerWon)
    {
        var challengers = iAmChallenger ? MyHeroes : RivalHeroes;
        var defenders = iAmChallenger ? RivalHeroes : MyHeroes;
        var duels = Enumerable.Range(0, 3).Select(i => new SquadDuelDto(
            i, challengers[i], defenders[i],
            // Slots 0 and 2 to the winner, slot 1 to the loser — a 2-1 that is not a whitewash.
            i == 1
                ? Fight(challengerWon ? defenders[i].Id : challengers[i].Id, challengerWon ? challengers[i].Id : defenders[i].Id)
                : Fight(challengerWon ? challengers[i].Id : defenders[i].Id, challengerWon ? defenders[i].Id : challengers[i].Id)))
            .ToList();

        return new SquadMatchDto(
            "sq-1",
            challengers.Select(h => h.Id).ToList(),
            defenders.Select(h => h.Id).ToList(),
            WagerSats: 1_000,
            Status: "resolved",
            Result: new SquadResultDto(challengerWon, challengerWon ? 2 : 1, challengerWon ? 1 : 2, duels));
    }

    private static PageTestContext SignedSquad(params SquadMatchDto[] matches)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", MyHeroes);
        ctx.Api.Get("/api/heroes", MyHeroes.Concat(RivalHeroes));
        ctx.Api.Get("/api/squad", matches);
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    /// <summary>
    /// The losing side's row was a bare "resolved" chip: no score, no winner, nothing. Every one of those
    /// facts was already sitting in <c>SquadMatchDto.Result</c> on the very list the row was drawn from.
    /// </summary>
    [Fact]
    public void Squad_TheLoserSeesTheScoreAndThatTheyLost()
    {
        using var ctx = SignedSquad(ResolvedSquad(iAmChallenger: false, challengerWon: true));

        var cut = ctx.Render<Squad>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.Contains("Past squad matches", cut.Markup);
        Assert.Contains(">lost<", cut.Markup);
        Assert.Contains("1–2", cut.Markup);   // my wins – theirs, stated from MY side of the table
    }

    /// <summary>
    /// The same row read from the winning DEFENDER's side. Separate from the test above because the score
    /// and the verdict are both perspective-dependent: the DTO speaks in challenger/defender, the row must
    /// speak in mine/theirs, and a fix that hard-codes either one is wrong for half the players.
    /// </summary>
    [Fact]
    public void Squad_TheDefenderWhoWonIsNotToldTheyLost()
    {
        using var ctx = SignedSquad(ResolvedSquad(iAmChallenger: false, challengerWon: false));

        var cut = ctx.Render<Squad>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.Contains(">won<", cut.Markup);
        Assert.DoesNotContain(">lost<", cut.Markup);
        Assert.Contains("2–1", cut.Markup);
    }

    /// <summary>
    /// And the loser can WATCH it. The three duels — both lineups' snapshots and every blow — ride along
    /// on the list DTO, so the side that did not press "Fight!" was being denied a replay the browser
    /// already held. No fetch, no new endpoint: the same <c>BattleArena</c> block, pointed at the row.
    /// </summary>
    [Fact]
    public void Squad_TheLoserCanWatchTheMatchTheyLost()
    {
        using var ctx = SignedSquad(ResolvedSquad(iAmChallenger: false, challengerWon: true));

        var cut = ctx.Render<Squad>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));

        var watch = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == "Watch");
        Assert.NotNull(watch);
        watch.Click();

        cut.WaitForAssertion(() => Assert.Contains("duel 1 of 3", cut.Markup));
        Assert.Contains("class=\"arena\"", cut.Markup);
    }
}
