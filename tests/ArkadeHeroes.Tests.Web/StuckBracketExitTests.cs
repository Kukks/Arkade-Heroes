using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The way out of a bracket that will never fill has to be ON THE PAGE.
///
/// <para>Tournament entries are one per player — deliberately, so a single wallet cannot buy every seat and
/// play itself for the pot — which means a bracket needs as many distinct PLAYERS as it has seats. Two
/// players therefore cannot fill a size-4 one however long they wait, and a live walk left exactly that: a
/// real bracket parked at 2/4 with 2,000 real sats escrowed in it.</para>
///
/// <para>The server side of this is <c>TournamentStuckBracketTests</c>. This is the other half, and on its
/// own it is the half that decides whether the fix is real: a refund endpoint nobody can reach from the
/// game is not an exit, it is a support ticket. The page rendered that bracket as "waiting for entrants"
/// and offered nothing at all — so the only way to a player's own escrowed sats ran through an operator.</para>
/// </summary>
public class StuckBracketExitTests
{
    private const string Me = "player-1";
    private const string Rival = "player-2";
    private const string MyHero = "hero-mine";
    private const string RivalHero = "hero-rival";

    private static HeroDto Mine => Fixtures.Hero(MyHero, "Ashfang");
    private static HeroDto Theirs => Fixtures.Hero(RivalHero, "Direbloom", ownerId: Rival);

    /// <summary>A size-4 bracket I have paid into, sitting at 2/4 — the stuck state, as the server reports it.</summary>
    private static TournamentDto StuckAtTwoOfFour(string status = "open") =>
        new("t-1", OpenerPlayerId: Me, BuyInSats: 1_000, Size: 4, Joined: 2, status,
            Entrants: new[] { new TournamentEntrantDto(Me, MyHero), new TournamentEntrantDto(Rival, RivalHero) },
            ChampionHeroId: null, ChampionPrizeSats: 0, EntrantsCommitmentHex: null);

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

    /// <summary>Takes the buttons rather than the rendered page: bUnit's rendered-component type is not
    /// public, and the exit affordance is identified by what it SAYS anyway.</summary>
    private static IElement? CallOff(IEnumerable<IElement> buttons) =>
        buttons.FirstOrDefault(b => b.TextContent.Contains("Call it off"));

    /// <summary>
    /// The affordance exists at all — the whole defect on the client side. The row used to say "waiting for
    /// entrants" and stop there, which is true and useless: it describes the trap without naming the door.
    /// </summary>
    [Fact]
    public void AnEntrantOfABracketThatCannotFill_IsOfferedAWayOut()
    {
        using var ctx = SignedTournaments(StuckAtTwoOfFour());

        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));

        Assert.NotNull(CallOff(cut.FindAll("button")));
    }

    /// <summary>
    /// And it is wired to the endpoint that actually moves the sats. Asserted on the REQUEST rather than on
    /// any rendered text, because a button that looks right and calls nothing is precisely the failure this
    /// page has already shipped once (a Verify click that ran to completion and drew no pixels).
    /// </summary>
    [Fact]
    public void CallingOffAStuckBracket_AsksTheServerForTheRefund()
    {
        using var ctx = SignedTournaments(StuckAtTwoOfFour());
        ctx.Api.Post("/api/tournament/t-1/refund", new TournamentRefundResponse(
            StuckAtTwoOfFour("refunded"), EntrantsRefunded: 2, RefundedSats: 2_000));

        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));

        CallOff(cut.FindAll("button"))!.Click();

        cut.WaitForAssertion(() => Assert.Contains("POST /api/tournament/t-1/refund", ctx.Api.Requested));
    }

    /// <summary>
    /// The line the button must not cross, mirrored from the server's own gate. Once a bracket is FULL the
    /// field is locked and committed and it is about to be fought for real sats — an exit offered THEN would
    /// be a free look at the draw followed by a way to never lose. The page must not even appear to offer it.
    /// </summary>
    [Fact]
    public void AFullBracketIsNotOfferedAWayOut()
    {
        using var ctx = SignedTournaments(StuckAtTwoOfFour("full"));

        var cut = ctx.Render<Tournaments>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading brackets", cut.Markup));

        Assert.Null(CallOff(cut.FindAll("button")));
    }
}
