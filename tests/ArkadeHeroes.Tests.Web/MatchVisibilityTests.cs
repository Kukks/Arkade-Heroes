using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// REGRESSION 3 — a match in <c>accepted</c> state disappearing from the DEFENDER's view.
///
/// <para>Both match pages sort the player's matches into buckets by (status, which side is mine). The
/// buckets were: open+defender ("accept this"), accepted+CHALLENGER ("resolve it"), open+challenger
/// ("waiting"). Nothing covered accepted+DEFENDER — so the instant a defender accepted, their match
/// belonged to no bucket and vanished from their page, while the challenger's copy stayed and grew a
/// resolve button. Accepting appeared to do nothing, and the two players saw different games.</para>
///
/// <para>This is the exact shape of bug a green unit suite cannot see. The server was right the whole
/// time — <c>GET /matches</c> returned the match, with the correct status, to both players. The defect
/// lived entirely in which LINQ predicate the markup asked for, so only rendering the page finds it.</para>
///
/// <para>The buckets are a partition, so each page is also pinned against the general case below: no
/// match involving one of my heroes may fall through every bucket, in ANY status.</para>
/// </summary>
public class MatchVisibilityTests
{
    private const string Mine = "hero-mine";
    private const string Theirs = "hero-theirs";

    private static PageTestContext Signed(params DeathMatchDto[] deathMatches)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashfang") });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Direbloom", ownerId: "player-2"));
        ctx.Api.Get("/api/deathmatch", deathMatches);
        return ctx;
    }

    // ── Death-match ──────────────────────────────────────────────────────────

    /// <summary>
    /// The defender accepted; the match is live and their hero is staked to the death. The page must
    /// still show it.
    /// </summary>
    [Fact]
    public void DeathMatch_AnAcceptedMatchIsStillVisibleToTheDefender()
    {
        using var ctx = Signed(Fixtures.DeathMatch("dm-1", Theirs, Mine, status: "accepted"));

        var cut = ctx.Render<DeathMatch>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));

        // The whole defect in one assert: the page had a live death-match and said there were none.
        Assert.DoesNotContain("No open death-matches", cut.Markup);
        Assert.Contains("Ashfang", cut.Markup);
    }

    /// <summary>The challenger's side of the same match was never broken — pinned so a fix to the
    /// defender's bucket cannot be made by breaking this one.</summary>
    [Fact]
    public void DeathMatch_AnAcceptedMatchIsStillVisibleToTheChallenger()
    {
        using var ctx = Signed(Fixtures.DeathMatch("dm-1", Mine, Theirs, status: "accepted"));

        var cut = ctx.Render<DeathMatch>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.DoesNotContain("No open death-matches", cut.Markup);
        Assert.Contains("Settle", cut.Markup);
    }

    /// <summary>
    /// The general form of the bug, so the next status added to the server cannot re-open it silently:
    /// a match involving one of my heroes, in ANY status the server can report, must appear somewhere.
    /// </summary>
    [Theory]
    [InlineData("open", true)]
    [InlineData("open", false)]
    [InlineData("accepted", true)]
    [InlineData("accepted", false)]
    public void DeathMatch_NoMatchOfMineFallsThroughEveryBucket(string status, bool iAmChallenger)
    {
        var dm = iAmChallenger
            ? Fixtures.DeathMatch("dm-1", Mine, Theirs, status)
            : Fixtures.DeathMatch("dm-1", Theirs, Mine, status);

        using var ctx = Signed(dm);

        var cut = ctx.Render<DeathMatch>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.DoesNotContain("No open death-matches", cut.Markup);
    }

    // ── Duel ─────────────────────────────────────────────────────────────────

    private static PageTestContext SignedDuel(params MatchDto[] matches)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashfang") });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Direbloom", ownerId: "player-2"));
        ctx.Api.Get("/api/matches", matches);
        return ctx;
    }

    private static MatchDto Match(string challenger, string defender, string status) =>
        new("m-1", challenger, defender, status, CommitmentHex: "00", Result: null, WagerSats: 1_000);

    /// <summary>The duel page carries the fix already; this holds it in place.</summary>
    [Fact]
    public void Duel_AnAcceptedMatchIsStillVisibleToTheDefender()
    {
        using var ctx = SignedDuel(Match(Theirs, Mine, "accepted"));

        var cut = ctx.Render<Duel>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.DoesNotContain("No open matches", cut.Markup);
        Assert.Contains("Ashfang", cut.Markup);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("open", false)]
    [InlineData("accepted", true)]
    [InlineData("accepted", false)]
    public void Duel_NoMatchOfMineFallsThroughEveryBucket(string status, bool iAmChallenger)
    {
        var m = iAmChallenger ? Match(Mine, Theirs, status) : Match(Theirs, Mine, status);

        using var ctx = SignedDuel(m);

        var cut = ctx.Render<Duel>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the arena", cut.Markup));
        Assert.DoesNotContain("No open matches", cut.Markup);
    }
}
