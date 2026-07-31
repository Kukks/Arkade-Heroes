namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// Every page in the game, opened in a real browser against a real running server.
///
/// <para>The suite next door already walks five routes, but it walks them with the API aborted at the
/// network layer, so what it proves is "this page draws when it has no data". That is worth having and it
/// is not the same claim: a page whose read failed and a page whose read returned nothing look identical
/// from the outside, and the load-bearing half of every route here — does it survive REAL data, does it
/// survive the round trip, does it throw on the way — is invisible from there.</para>
///
/// <para>So these open the same bundle with <c>ArkadeHeroes.Server</c> behind it. Each route is asserted on
/// three axes that a 200 does not cover: it left the boot screen, Blazor's unhandled-error banner is not
/// up, and NOTHING reached the console. The third is the one that matters most and the one nothing else in
/// this repo checks — the crash that blanked every content-pack page announced itself in the console first,
/// and a page that renders while throwing is a defect that ships.</para>
/// </summary>
[Collection(PlayableAppCollection.Name)]
public class ArenaWalkTests(PlayableAppFixture app)
{
    /// <summary>
    /// Every route the app declares, less the two that need an id (covered by
    /// <see cref="SeededArenaTests"/>, which can make one).
    ///
    /// <para>Enumerated by hand on purpose. A test that discovered routes by reflection would follow the app
    /// wherever it went, including into deleting one — the list is here so that removing a page is a diff
    /// somebody has to justify.</para>
    /// </summary>
    public static TheoryData<string> EveryRoute =>
    [
        "/", "/play", "/heroes", "/account", "/breed", "/achievements", "/leaderboard", "/reclaim",
        "/market", "/sell", "/codex", "/tech", "/wallet", "/spar", "/gauntlet", "/trials", "/duel",
        "/deathmatch", "/merge", "/gear", "/daily", "/squad", "/tournaments", "/terms",
        // Not in the nav, but routable, so a player can reach them and a defect can hide in them.
        "/studio", "/admin", "/not-found",
    ];

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task EveryPageRendersWithoutThrowing(string route)
    {
        var session = await app.OpenAsync(route);

        await session.AssertHealthyAsync(route);

        // A blank page satisfies all three checks above — Blazor is perfectly happy to render nothing — so
        // the route has to have put something on screen as well.
        Assert.NotEmpty((await session.BodyTextAsync()).Trim());
    }

    /// <summary>
    /// A deep link is a page load, not a client-side transition, and the two take different paths through
    /// the server: the router resolves the second, and Program.cs's 404-rewriting middleware has to resolve
    /// the first. That middleware is narrow by design (GET/HEAD, never under /api, only when a bundle is
    /// present) and it is the only reason a bookmarked hero page works at all.
    ///
    /// <para>Pinned HERE rather than only in SpaFallbackTests because that suite serves a stub index.html to
    /// a bare HttpClient. It proves the right bytes are returned; it cannot prove the browser then boots the
    /// app and routes to the page, which is the part a player experiences.</para>
    /// </summary>
    [Fact]
    public async Task ADeepLinkBootsTheAppAndLandsOnTheRightPage()
    {
        var session = await app.OpenAsync("/codex");
        await session.AssertHealthyAsync("/codex deep link");

        Assert.Contains("Codex", await session.Page.TitleAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole premise of this fixture, asserted rather than assumed: the browser is really talking to the
    /// game server, on the origin it was served from.
    ///
    /// <para>Without this the suite could pass in its entirety while every page rendered its error state,
    /// which is precisely the failure the published bundle ships pre-loaded with — <c>appsettings.json</c>
    /// leaves the publish pointing <c>ApiBaseUrl</c> at <c>http://localhost:5210</c>, a port nothing in CI
    /// is listening on. The container entrypoint rewrites that file to make the app same-origin, the fixture
    /// reproduces the rewrite, and this test is what would notice if either stopped working.</para>
    /// </summary>
    [Fact]
    public async Task ThePagesAreTalkingToThisServerOnItsOwnOrigin()
    {
        var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var apiCalls = new List<string>();
        page.Request += (_, r) => { if (r.Url.Contains("/api/", StringComparison.Ordinal)) apiCalls.Add(r.Url); };

        await page.GotoAsync($"{app.BaseUrl}/heroes",
            new() { WaitUntil = Microsoft.Playwright.WaitUntilState.NetworkIdle });
        await page.WaitForSelectorAsync(".page-head", new() { Timeout = 60_000 });

        Assert.NotEmpty(apiCalls);
        Assert.All(apiCalls, url => Assert.StartsWith(app.BaseUrl, url, StringComparison.Ordinal));
    }
}
