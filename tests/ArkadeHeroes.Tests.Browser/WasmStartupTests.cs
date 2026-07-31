using Microsoft.Playwright;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// The residue bUnit cannot reach: does the PUBLISHED bundle actually start in a real browser.
///
/// <para>Everything here is about the artifact, not the logic. A render test proves a component draws
/// given services; it says nothing about whether the IL linker removed a type the app needs at startup,
/// whether the boot manifest resolves, or whether a static initializer throws before the first component
/// exists. Those failures are total — a blank page — and one has shipped: the TypeInitializationException
/// that took out every page touching the content pack. It was read as a trimming problem, and #207
/// established it was really a static-initializer cycle in ContentPackVersion. The part that matters here
/// survives either diagnosis: a local build and the whole unit suite stayed green throughout, because
/// nothing in them ever started the app.</para>
///
/// <para>So this suite exists to make that class observable. It is deliberately small: startup, the
/// unhandled-error surface, and the pages whose statics are the ones that actually went down.</para>
/// </summary>
[Collection(PublishedAppCollection.Name)]
public class WasmStartupTests(PublishedAppFixture app)
{
    /// <summary>Content of the pre-boot screen in index.html. While this is on screen, .NET has not
    /// started; if it is still there after the wait, the runtime never came up.</summary>
    private const string BootScreenText = "inserting coin";

    private async Task<(IPage Page, List<string> Errors)> OpenAsync(string path, bool offlineBackends = false)
    {
        var context = await app.Browser.NewContextAsync();
        var page = await context.NewPageAsync();

        if (offlineBackends)
        {
            // Everything the bundle dials that is NOT the bundle's own origin: arkd, esplora, the game
            // API. Aborting them simulates a player whose backends are down or unreachable.
            await page.RouteAsync("**", async route =>
            {
                if (route.Request.Url.StartsWith(app.BaseUrl, StringComparison.Ordinal))
                    await route.ContinueAsync();
                else
                    await route.AbortAsync();
            });
        }

        var errors = new List<string>();
        // A .NET exception that escapes to the browser arrives as a page error; a failed boot usually also
        // logs to the console first. Both are collected because either alone can be the only symptom.
        page.PageError += (_, e) => errors.Add(e);
        page.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };

        await page.GotoAsync($"{app.BaseUrl}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        return (page, errors);
    }

    /// <summary>
    /// The frontend must boot even when arkd, esplora and the game API are all unreachable.
    ///
    /// <para>Worth pinning on its own — a player on a flaky connection, or an operator whose node is down,
    /// should get a page that says something rather than a permanent "inserting coin". Program.cs starts
    /// the Ark SDK services BEFORE the host runs, so a start path that threw on an unreachable node would
    /// mean no UI at all, and the failure would look identical to a broken deploy.</para>
    ///
    /// <para>It is also what lets this suite run without the regtest stack, which is why it is here rather
    /// than assumed.</para>
    /// </summary>
    [Fact]
    public async Task TheBundleStillBootsWhenTheBackendsAreUnreachable()
    {
        var (page, _) = await OpenAsync("/", offlineBackends: true);

        await page.WaitForSelectorAsync("nav, .page-head, main",
            new() { Timeout = 60_000, State = WaitForSelectorState.Attached });

        var body = await page.InnerTextAsync("body");
        Assert.DoesNotContain(BootScreenText, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole point in one test: the published bundle boots and replaces its own loading screen.
    /// </summary>
    [Fact]
    public async Task ThePublishedBundleBootsInARealBrowser()
    {
        var (page, _) = await OpenAsync("/");

        // The app shell rendering at all is the proof the runtime started and the root component ran.
        await page.WaitForSelectorAsync("nav, .page-head, main",
            new() { Timeout = 60_000, State = WaitForSelectorState.Attached });

        var body = await page.InnerTextAsync("body");
        Assert.DoesNotContain(BootScreenText, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Blazor's own unhandled-exception surface. It is hidden by CSS until the runtime shows it, so a
    /// VISIBLE #blazor-error-ui means something threw where nothing caught it — the exact end state of the
    /// content-pack startup failure this project exists to notice.
    /// </summary>
    [Fact]
    public async Task StartupRaisesNoUnhandledError()
    {
        var (page, errors) = await OpenAsync("/");
        await page.WaitForSelectorAsync("nav, .page-head, main",
            new() { Timeout = 60_000, State = WaitForSelectorState.Attached });

        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(),
            "Blazor's unhandled-error banner is showing on a freshly loaded page");

        Assert.DoesNotContain(errors, e => e.Contains("TypeInitializationException", StringComparison.Ordinal));
    }

    /// <summary>
    /// The pages whose content-pack statics are the known-fragile ones. /gauntlet resolves
    /// <c>ContentPack.Default.FindDungeon("gauntlet")</c> in a static initializer — the one that actually
    /// threw — and /codex and /play read the same pack. When it goes, these render blank.
    /// </summary>
    [Theory]
    [InlineData("/play")]
    [InlineData("/gauntlet")]
    [InlineData("/codex")]
    [InlineData("/heroes")]
    [InlineData("/wallet")]
    public async Task ContentBackedRoutesRenderInThePublishedBundle(string route)
    {
        var (page, _) = await OpenAsync(route);

        await page.WaitForSelectorAsync("nav, .page-head, main",
            new() { Timeout = 60_000, State = WaitForSelectorState.Attached });

        var body = await page.InnerTextAsync("body");
        Assert.DoesNotContain(BootScreenText, body, StringComparison.OrdinalIgnoreCase);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(),
            $"{route} raised an unhandled error in the published bundle");
        Assert.NotEmpty(body.Trim());
    }
}
