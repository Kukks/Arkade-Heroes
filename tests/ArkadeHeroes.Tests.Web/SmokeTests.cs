using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The cheapest gate this project buys: every page below is RENDERED, signed out and signed in, and an
/// exception out of a lifecycle method fails the test.
///
/// <para>Worth having on its own. Until this project existed nothing rendered a page, so a null
/// dereference or a bad cast in <c>OnInitializedAsync</c> compiled clean, passed the whole unit suite,
/// and was found by a person clicking on it.</para>
/// </summary>
public class SmokeTests
{
    /// <summary>Signed out, a page must render its "press Play" prompt rather than throw. This is the
    /// state every first-time visitor arrives in.</summary>
    [Fact]
    public void SignedOut_MatchPagesRenderTheirPrompt()
    {
        using var ctx = new PageTestContext();

        Assert.Contains("Press", ctx.Render<Duel>().Markup);
        Assert.Contains("Press", ctx.Render<DeathMatch>().Markup);
    }

    /// <summary>Signed in with a roster, the pages that read one must render it.</summary>
    [Fact]
    public void SignedIn_MatchPagesRenderTheRoster()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero("h1", "Ashfang") });
        ctx.Api.Get("/api/matches", Array.Empty<MatchDto>());
        ctx.Api.Get("/api/deathmatch", Array.Empty<DeathMatchDto>());

        var duel = ctx.Render<Duel>();
        duel.WaitForAssertion(() => Assert.Contains("Ashfang", duel.Markup));

        var dm = ctx.Render<DeathMatch>();
        dm.WaitForAssertion(() => Assert.Contains("Ashfang", dm.Markup));
    }

    /// <summary>The static pages — no services, no network, so a failure here is a Razor or content-pack
    /// problem rather than a data one.</summary>
    [Fact]
    public void StaticPagesRender()
    {
        using var ctx = new PageTestContext();

        Assert.Contains("What do you want to do?", ctx.Render<Play>().Markup);
        Assert.NotEmpty(ctx.Render<NotFound>().Markup);
    }

    /// <summary>
    /// Opening a page must never BILL anything. The roster quotes a recruit price, which is a read — if
    /// that ever became the POST that mints an invoice, merely visiting /heroes would start costing sats.
    /// </summary>
    [Fact]
    public void OpeningTheRosterDoesNotPostAnything()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes", Array.Empty<HeroDto>());
        ctx.Api.Get("/api/heroes/mine", Array.Empty<HeroDto>());
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());

        var cut = ctx.Render<Heroes>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the roster", cut.Markup));

        Assert.DoesNotContain(ctx.Api.Requested, r => r.StartsWith("POST"));
    }
}
