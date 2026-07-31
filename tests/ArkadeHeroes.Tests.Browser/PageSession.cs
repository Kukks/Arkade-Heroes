using Microsoft.Playwright;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// One browser page plus everything it complained about while it was open.
///
/// <para>The pairing is the point. "The page rendered" and "the page rendered without throwing" are
/// different claims, and only the second one is worth anything: the gauntlet crash in #207 drew a page.
/// Bundling the console with the page means a test cannot assert the first while forgetting the second.</para>
/// </summary>
public sealed class PageSession(IPage page, string baseUrl)
{
    /// <summary>Content of the pre-boot screen in index.html. While this is on screen, .NET has not started.</summary>
    private const string BootScreenText = "inserting coin";

    /// <summary>Everything the browser complained about, verbatim and in order, harness-caused included.
    /// Reported in failure messages so a filtered-out error is still visible when something else breaks.</summary>
    public List<string> Errors { get; } = [];

    public IPage Page => page;

    /// <summary>
    /// The errors the APP raised — what an assertion should actually look at.
    ///
    /// <para>This suite holds the player's Ark node unreachable (see <see cref="PlayableAppFixture"/>),
    /// because a hermetic harness has no arkd and a suite whose result depends on whether one happens to be
    /// listening is not a gate. A refused request is a console error, and the SDK's background batch stream
    /// then retries and logs on a timer, so that one condition produces a steady trickle of noise that has
    /// nothing to do with the page under test.</para>
    ///
    /// <para>Excluded as narrowly as the evidence allows: by the ORIGIN a console message came from, and by
    /// <c>NArk.Transport.RestClient</c> appearing in a logged stack — the arkd REST transport, which no game
    /// API call passes through. A blanket "ignore network errors" or "ignore anything from NArk" would have
    /// swallowed a broken /api call or a real wallet defect, and catching those is most of the point.</para>
    /// </summary>
    public IReadOnlyList<string> AppErrors =>
        Errors.Where(e => !IsUnreachableArkNode(e) && !IsDocumentedAbsenceProbe(e)).ToList();

    private static bool IsUnreachableArkNode(string message) =>
        PlayableAppFixture.WalletBackends.Any(b => message.Contains(b, StringComparison.OrdinalIgnoreCase))
        || message.Contains("NArk.Transport.RestClient", StringComparison.Ordinal);

    /// <summary>
    /// Chromium logs a console error for every failed fetch, including ones the app made on purpose and
    /// handled. A hero's headstone is asked for before its detail page renders precisely so a BURNED hero
    /// shows a grave rather than "couldn't load this hero", and 404 is the ordinary answer for a living one
    /// — HeroDetail.razor says exactly that where it catches it.
    ///
    /// <para>Listed by exact path, not by status. "Ignore 404s" would have hidden a page requesting a route
    /// that no longer exists, and a second probe earning an exemption should cost somebody a line here and
    /// a reason for it — which is the whole value of the list being short.</para>
    /// </summary>
    private static bool IsDocumentedAbsenceProbe(string message) =>
        message.Contains("/tombstone", StringComparison.Ordinal)
        && message.Contains("404", StringComparison.Ordinal);

    /// <summary>Navigate within the same page (and the same storage, so the same wallet and session).</summary>
    public async Task GotoAsync(string path)
    {
        await page.GotoAsync($"{baseUrl}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await WaitForAppAsync();
    }

    /// <summary>Waits until the root component has drawn something — the proof the runtime started.</summary>
    public async Task WaitForAppAsync()
    {
        await page.WaitForSelectorAsync(
            "nav, .page-head, main", new() { Timeout = 60_000, State = WaitForSelectorState.Attached });
        await WaitForQuietAsync();
    }

    /// <summary>
    /// Waits for the app's own loading indicator to clear.
    ///
    /// <para>Needed because neither of the two obvious signals means what a test wants. The page head draws
    /// before any data is asked for — every page in this app puts its title above its fetch — so waiting on
    /// it lands mid-load. And network-idle fires 500ms after the last request, which a page that starts its
    /// fetch from <c>OnParametersSetAsync</c> can beat. Asserting on either produced a roster that passed and
    /// a hero page that failed for the same reason.</para>
    ///
    /// <para><c>.dot.wait</c> is the house spinner: "loading the roster…", "browsing the stalls…",
    /// "tallying the boards…". Tolerant of a timeout rather than throwing on one, because a page is allowed
    /// to keep a spinner up for something a test never asked about — the assertions that follow are what
    /// decide whether the page is right, and they say so much better than a timeout would.</para>
    /// </summary>
    private async Task WaitForQuietAsync()
    {
        try
        {
            await page.Locator(".dot.wait").First
                .WaitForAsync(new() { Timeout = 20_000, State = WaitForSelectorState.Detached });
        }
        catch (TimeoutException) { /* still busy, or never was; let the assertions speak */ }
    }

    public Task<string> BodyTextAsync() => page.InnerTextAsync("body");

    /// <summary>
    /// The three ways a page can be broken while still returning 200: it never booted, Blazor caught
    /// something nobody handled, or it threw into the console on the way to drawing.
    /// </summary>
    public async Task AssertHealthyAsync(string what)
    {
        var body = await BodyTextAsync();

        Assert.False(body.Contains(BootScreenText, StringComparison.OrdinalIgnoreCase),
            $"{what}: the app never left the boot screen.");

        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(),
            $"{what}: Blazor's unhandled-error banner is showing.\nErrors:\n  {string.Join("\n  ", Errors)}");

        Assert.True(AppErrors.Count == 0,
            $"{what}: the page rendered but raised {AppErrors.Count} uncaught error(s):\n  "
            + string.Join("\n  ", AppErrors));
    }
}
