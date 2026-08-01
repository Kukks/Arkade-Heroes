using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A duel has two players and one of them runs the fight. The DEFENDER's page was a photograph.
///
/// <para>Observed in a live two-browser walk: the challenger pressed "Duel!", the match resolved, sats
/// moved and XP settled — and the defender's tab still read "Accepted — waiting for the challenger to
/// fight" until they refreshed by hand. Nothing on that page was wrong at the time it was drawn; it was
/// drawn once, and duels are resolved by the other person. So the page had no way to ever be right again,
/// and the one player who is not in control of the outcome is the one who cannot see it.</para>
///
/// <para><see cref="ArkadeHeroes.Web.Components.ChallengeAlert"/> already established the pattern this
/// uses — a timer, because there is no live channel to the server and adding one for this would be a lot
/// of machinery for a fact that can be seconds stale without hurting anyone.</para>
/// </summary>
public class DuelFreshnessTests
{
    private const string Mine = "hero-mine";
    private const string Theirs = "hero-theirs";
    private const string Stale = "waiting for the challenger to fight";

    /// <summary>Fast enough that a test is not a wall-clock hostage; the live cadence is the page's default.</summary>
    private static readonly TimeSpan Brisk = TimeSpan.FromMilliseconds(80);

    private static PageTestContext Signed(params MatchDto[] matches)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashfang") });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Direbloom", ownerId: "player-2"));
        ctx.Api.Get("/api/matches", matches);
        return ctx;
    }

    private static MatchDto Accepted() =>
        new("m-1", Theirs, Mine, "accepted", CommitmentHex: "00", Result: null, WagerSats: 1_000);

    /// <summary>The same match after the challenger resolved it — their hero won, so mine lost.</summary>
    private static MatchDto Resolved() =>
        new("m-1", Theirs, Mine, "resolved", CommitmentHex: "00",
            Result: new BattleResultDto(Theirs, Mine, Turns: 4, Events: [], WinnerRemainingHp: 30, WinnerMaxHp: 100),
            WagerSats: 1_000);

    [Fact]
    public void TheDefendersPageFollowsTheChallengersResolution_WithoutAManualRefresh()
    {
        using var ctx = Signed(Accepted());

        var cut = ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, Brisk));
        cut.WaitForAssertion(() => Assert.Contains(Stale, cut.Markup));

        // The challenger presses "Duel!" in their own browser. Nothing at all tells this one.
        ctx.Api.Get("/api/matches", new[] { Resolved() });

        cut.WaitForAssertion(() => Assert.DoesNotContain(Stale, cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("Past duels", cut.Markup);
        Assert.Contains("lost", cut.Markup);   // my hero was the loser, and the page says so
    }

    /// <summary>
    /// The challenger's own page is refreshed by the action that resolved the duel, so it was never the
    /// broken side — pinned so the poll cannot be "fixed" by breaking it.
    /// </summary>
    [Fact]
    public void TheChallengersPageStillShowsAResolveButtonUntilItIsResolved()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashfang") });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Direbloom", ownerId: "player-2"));
        ctx.Api.Get("/api/matches", new[]
        {
            new MatchDto("m-1", Mine, Theirs, "accepted", "00", Result: null, WagerSats: 1_000),
        });

        var cut = ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, Brisk));

        cut.WaitForAssertion(() => Assert.Contains("Duel!", cut.Markup));
        Assert.DoesNotContain(Stale, cut.Markup);
    }

    // NOT TESTED HERE, deliberately: that a slow older poll cannot land on top of a newer one.
    //
    // LoadMatchesAsync carries the same _matchesSeq guard the roster page carries (and
    // HeroesDeepLinkTests.ASlowAllResponseCannotLandOnTopOfTheMineListItPreceded does pin), but the
    // equivalent test could not be made to bite here and is not present rather than present-and-vacuous.
    // The reason is worth knowing: on the roster a superseded read is TERMINAL — nothing reads it again,
    // so the wrong list stays on screen and an assertion catches it. A poll by definition runs again, so
    // the stale list is repaired one interval later and the defect is transient by construction. Watching
    // the markup continuously through the window still did not catch it reliably. The guard stays because
    // it is three lines and correct; the claim that a test proves it does not.

    /// <summary>
    /// Navigating away must actually stop the poll.
    ///
    /// <para>ChallengeAlert can write its timer as a bare <c>async _ => await InvokeAsync(...)</c> because
    /// it rides the shell and is never disposed. A PAGE is, on every navigation, so this one has to hand
    /// its timer back — a page left polling after the player walked away is a request loop nobody can see
    /// and nobody can stop.</para>
    ///
    /// <para>This pins the observable half: the requests stop. The <c>ObjectDisposedException</c> catch in
    /// <c>PollTickAsync</c> covers the narrower race of a tick already dispatched at the instant of
    /// disposal, which is not deterministically reachable from here — it is defensive, and said so.</para>
    /// </summary>
    [Fact]
    public void DisposingThePageStopsThePoll()
    {
        var ctx = Signed(Accepted());
        var api = ctx.Api;

        var cut = ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, Brisk));
        cut.WaitForAssertion(() => Assert.Contains(Stale, cut.Markup));

        ctx.Dispose();   // what navigating away does
        var atDisposal = api.Requested.Count;

        // Several poll intervals go by with the page gone.
        System.Threading.Thread.Sleep(500);

        // A RATE, not an equality — and that is the whole point of this shape.
        //
        // A tick already dispatched at the instant of disposal cannot be recalled: it is in flight, it
        // will record itself, and no amount of waiting before sampling makes that deterministic. Asserting
        // the count never moves failed about half the CI runs on main (f0194f3 and d1bf548 red, c725c66
        // and 94bfc76 green) while passing every time locally, which is how a timing race presents. Adding
        // a settle delay first did not fix it either — under contention the straggler simply lands later.
        //
        // So tolerate exactly one straggler and measure the thing that actually distinguishes the defect:
        // a timer that OUTLIVED the page keeps firing every PollEvery, which is roughly six requests
        // across this window, not one. Proven to still bite by commenting out _poll?.Dispose() in
        // Duel.razor, which turns this red.
        var landed = api.Requested.Count - atDisposal;
        Assert.True(landed <= 1,
            $"the poll kept running after the page was disposed: {landed} requests landed in 500ms "
            + $"({Brisk.TotalMilliseconds}ms interval), so the timer outlived its page.");
    }

    /// <summary>
    /// A poll that fails must leave the page as it was. "We could not re-read your matches" is not news
    /// worth blanking a correct screen for, and turning a live duel into "Couldn't read your matches" on a
    /// single dropped request would be a worse page than the stale one this replaces.
    /// </summary>
    [Fact]
    public void AFailedPollLeavesTheLastGoodMatchListStanding()
    {
        using var ctx = Signed(Accepted());

        var cut = ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, Brisk));
        cut.WaitForAssertion(() => Assert.Contains(Stale, cut.Markup));

        ctx.Api.GetFails("/api/matches");

        // Give the poll several turns to do damage, then check it did none.
        System.Threading.Thread.Sleep(500);
        cut.WaitForAssertion(() => Assert.Contains(Stale, cut.Markup));
        Assert.DoesNotContain("Couldn't read your matches", cut.Markup);
    }
}
