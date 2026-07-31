using Microsoft.Playwright;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// The first minute of a new player's game, driven by clicking the things they would click.
///
/// <para><b>Where this stops, and why it is not a gap that can be closed here.</b> The onboarding path is
/// land → accept the terms → a wallet is provisioned → fund it → buy a hero. The first two steps are the
/// app's own; every step after them belongs to the wallet, and the wallet in this game is NON-CUSTODIAL and
/// talks to arkd DIRECTLY from the tab — no relay, no server in between. Creating one is not local key
/// generation: <c>GameWallet.ImportAsync</c> calls <c>transport.GetServerInfoAsync()</c> and derives the
/// wallet from the Ark server's own parameters, so with no node there is no wallet, no address and nothing
/// to fund. The claim after it is a real VTXO spend.</para>
///
/// <para>The server's in-memory chain does not help: it simulates the chain the SERVER sees, and the
/// <c>/api/dev</c> facade pays invoices on the server's behalf. The browser never calls it — it pays by
/// spending its own coins — so there is no configuration of this harness in which a browser buys a hero
/// without an Ark node. Standing up a stub arkd would make the test pass and prove nothing about the
/// covenant path it was standing in for; the funded flow is genuinely covered, against real arkd, by
/// <c>tests/ArkadeHeroes.Tests.E2E</c>.</para>
///
/// <para>So this walks to the wall and then asserts the thing that IS in scope and has bitten before: that
/// the app tells the truth when it gets there. A player whose node is unreachable must be told, not left on
/// a spinner and not shown a success they did not get.</para>
/// </summary>
[Collection(PlayableAppCollection.Name)]
public class NewPlayerWalkTests(PlayableAppFixture app)
{
    /// <summary>
    /// A stranger lands, and the page offers them the one thing it should: a name and a Play button.
    /// </summary>
    [Fact]
    public async Task AStrangerLandsOnSomethingTheyCanStart()
    {
        var session = await app.OpenAsync("/");
        await session.AssertHealthyAsync("/ as a first-time visitor");

        // Signed out in a fresh context, so there is no wallet in this tab's storage: the pill has to say
        // that rather than show a balance it does not have.
        Assert.Contains("no wallet", await session.Page.InnerTextAsync(".pill"), StringComparison.OrdinalIgnoreCase);

        Assert.True(await session.Page.Locator(".play-box input.wallet-input").IsVisibleAsync(),
            "a first-time visitor is not offered a box to put their arena name in");
        Assert.True(await session.Page.Locator(".play-box button.btn.primary").IsVisibleAsync(),
            "a first-time visitor is not offered a Play button");
    }

    /// <summary>
    /// Pressing Play stops for the Terms of Use, shows the actual document, and refuses to let anyone accept
    /// it before it is on screen.
    ///
    /// <para>This gate is the app's legal surface and it is built to fail CLOSED — Accept stays disabled
    /// until <c>TermsDocument</c> reports the prose rendered. That guard depends on a markdown file being
    /// BUNDLED as a static web asset, which is a publish-time property: the terms are copied into wwwroot by
    /// an MSBuild target, and getting that wrong once already produced a 200 with an empty body, a gate that
    /// could never be satisfied, and onboarding blocked outright. Nothing that renders components in-process
    /// can see it. This is the suite that can.</para>
    /// </summary>
    [Fact]
    public async Task PressingPlayStopsAtTheTermsAndTheTermsAreActuallyThere()
    {
        var session = await app.OpenAsync("/");

        await session.Page.FillAsync(".play-box input.wallet-input", "Walker");
        await session.Page.ClickAsync(".play-box button.btn.primary");

        var gate = session.Page.Locator(".modal.terms-modal");
        await gate.WaitForAsync(new() { Timeout = 30_000 });

        // The prose itself, not just the frame around it. An empty gate is the shipped failure.
        var terms = await session.Page.InnerTextAsync(".terms-scroll");
        Assert.True(terms.Trim().Length > 400,
            $"the terms gate opened with {terms.Trim().Length} characters of document in it — "
            + "the terms are not bundled, so nobody can accept them and nobody can play");
        Assert.DoesNotContain("v@Terms.CurrentVersion", await gate.InnerTextAsync(), StringComparison.Ordinal);

        // Fails closed the other way too: the checkbox is what unlocks Accept, and it starts unticked.
        var accept = session.Page.Locator(".terms-actions button.btn.primary");
        Assert.False(await accept.IsEnabledAsync(),
            "Accept was enabled before the player ticked the box confirming they had read the terms");

        await session.AssertHealthyAsync("the terms gate");
    }

    /// <summary>
    /// Accepting the terms with the player's Ark node unreachable must produce a stated failure — not a
    /// spinner that never resolves, and not a silent return to a page that looks like nothing happened.
    ///
    /// <para>This is the wall described on the class, and it is worth an assertion in its own right because
    /// it is a real player state: a node that is down, a laptop on a captive-portal wifi, a regtest stack
    /// nobody started. The app has an error line for it (<c>_playError</c>) and this is what proves the line
    /// is reached rather than merely written.</para>
    /// </summary>
    [Fact]
    public async Task AnUnreachableArkNodeFailsTheClaimOutLoudRatherThanHanging()
    {
        var session = await app.OpenAsync("/");

        await session.Page.FillAsync(".play-box input.wallet-input", "Nodeless");
        await session.Page.ClickAsync(".play-box button.btn.primary");

        await session.Page.Locator(".modal.terms-modal").WaitForAsync(new() { Timeout = 30_000 });
        await session.Page.CheckAsync(".terms-check input[type=checkbox]");
        await session.Page.ClickAsync(".terms-actions button.btn.primary");

        // Either outcome is honest; a spinner that never resolves is not. The wallet cannot be created
        // without the node, so what has to appear is the app saying so.
        var failure = session.Page.Locator(".play-box ~ .status-line .dot.bad, .status-line .dot.bad");
        await failure.First.WaitForAsync(new() { Timeout = 60_000 });

        var body = await session.BodyTextAsync();
        Assert.DoesNotContain("Summoning…", body, StringComparison.Ordinal);
        Assert.False(await session.Page.Locator("#blazor-error-ui").IsVisibleAsync(),
            "an unreachable Ark node took the page down with Blazor's unhandled-error banner instead of "
            + "being reported in the flow that caused it");
    }

    /// <summary>
    /// The nav goes where it says it goes, by clicking rather than by typing URLs.
    ///
    /// <para>A route test proves a page exists. It cannot prove a player can GET there, and the two come
    /// apart in ordinary ways: an href that resolves against the current document rather than the root, a
    /// section that never lights, a subnav that renders for the wrong page. The roster's one call to action
    /// for a signed-out visitor was exactly this bug — an empty href that re-navigated to the page it was
    /// already on.</para>
    /// </summary>
    [Theory]
    [InlineData("header nav a[href='play']", "/play", "What do you want to do?")]
    [InlineData("header nav a[href='market']", "/market", "Market")]
    [InlineData("a.pill", "/wallet", "Wallet")]
    [InlineData(".footer a[href='terms']", "/terms", "Terms")]
    public async Task TheNavTakesAPlayerWhereItSaysItDoes(string selector, string expectedPath, string expectedText)
    {
        var session = await app.OpenAsync("/");

        await session.Page.ClickAsync(selector);
        await session.Page.WaitForURLAsync($"**{expectedPath}", new() { Timeout = 30_000 });
        await session.WaitForAppAsync();

        Assert.Contains(expectedText, await session.BodyTextAsync(), StringComparison.OrdinalIgnoreCase);
        await session.AssertHealthyAsync($"{expectedPath} reached by clicking {selector}");
    }
}
