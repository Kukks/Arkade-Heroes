using ArkadeHeroes.Web.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// <c>/heroes?mine=1</c> is the link the rest of the app hands a player who needs their OWN roster — the
/// duel page's "you need a hero first", the recruit path after onboarding. The recruit card only renders
/// under Mine, so landing on All is landing on a grid of other people's creatures with nothing to press.
///
/// <para>It worked when clicked and failed when opened cold, which is the signature of a startup-ordering
/// bug rather than a routing one: the page read the query string in <c>OnInitializedAsync</c> and threw
/// the answer away unless sign-in had ALREADY resumed. On a cold WASM boot the shell resumes sign-in after
/// the first render, so the condition was false exactly once — on the load that came from the link.</para>
///
/// <para>The fix is to hold the link's intent until the state it depends on exists, not to wait longer
/// before reading it.</para>
/// </summary>
public class HeroesDeepLinkTests
{
    private const string Mine = "Ashfang";
    private const string NotMine = "Somebody Elses Hero";

    /// <summary>The Mine-only affordance the link exists to reach.</summary>
    private const string RecruitCard = "Recruit a hero";

    private static PageTestContext Arena()
    {
        var ctx = new PageTestContext();
        ctx.Api.Get("/api/heroes", new[] { Fixtures.Hero("hero-other", NotMine, ownerId: "player-2") });
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero("hero-mine", Mine) });
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    private static void Open(PageTestContext ctx, string url) =>
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);

    /// <summary>
    /// The cold boot: the page initialises, and only afterwards does the shell finish resuming sign-in.
    /// The link's intent has to survive that gap.
    /// </summary>
    [Fact]
    public void MineEqualsOne_OnAColdBoot_OpensMineOnceSignInResumes()
    {
        using var ctx = Arena();
        Open(ctx, "http://localhost/heroes?mine=1");

        var cut = ctx.Render<Heroes>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the roster", cut.Markup));

        // Signed out there is no "mine" to show, so All is the only honest answer at this instant.
        Assert.Contains(NotMine, cut.Markup);

        // The shell's deferred sign-in lands, exactly as it does on a real cold boot.
        cut.InvokeAsync(() => ctx.SignIn());

        cut.WaitForAssertion(() => Assert.Contains(Mine, cut.Markup), TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(NotMine, cut.Markup);
        Assert.Contains(RecruitCard, cut.Markup);
    }

    /// <summary>The case that always worked — SPA navigation, where sign-in is already up. Pinned so the
    /// cold-boot fix cannot be made by breaking the warm path.</summary>
    [Fact]
    public void MineEqualsOne_WhenAlreadySignedIn_OpensMineImmediately()
    {
        using var ctx = Arena();
        ctx.SignIn();
        Open(ctx, "http://localhost/heroes?mine=1");

        var cut = ctx.Render<Heroes>();

        cut.WaitForAssertion(() => Assert.Contains(Mine, cut.Markup));
        Assert.DoesNotContain(NotMine, cut.Markup);
        Assert.Contains(RecruitCard, cut.Markup);
    }

    /// <summary>No <c>?mine=1</c> means All, before and after sign-in. The held intent must not leak into
    /// every visit to /heroes — that would take the whole-game roster away from anyone who signs in.</summary>
    [Fact]
    public void WithoutTheQueryString_SignInDoesNotSwitchTheTab()
    {
        using var ctx = Arena();
        Open(ctx, "http://localhost/heroes");

        var cut = ctx.Render<Heroes>();
        cut.WaitForAssertion(() => Assert.Contains(NotMine, cut.Markup));

        cut.InvokeAsync(() => ctx.SignIn());

        cut.WaitForAssertion(() => Assert.Contains("All", cut.Markup));
        Assert.Contains(NotMine, cut.Markup);
        Assert.DoesNotContain(Mine, cut.Markup);
    }

    /// <summary>
    /// The second risk the fix introduces, and the sharper one: holding the intent means a MINE read now
    /// starts while the signed-out ALL read is still on the wire. Two roster reads are in flight at once,
    /// and if the page keeps whichever RETURNED last rather than whichever it ISSUED last, the slow ALL
    /// answer lands on top and fills the "Mine" tab with other people's heroes — the exact confusion
    /// <c>?mine=1</c> exists to prevent, reached from the other direction.
    /// </summary>
    [Fact]
    public void ASlowAllResponseCannotLandOnTopOfTheMineListItPreceded()
    {
        using var ctx = new PageTestContext();
        // The signed-out read is slow; the read sign-in triggers is not. So ALL is issued first and
        // arrives second, which is the only ordering that can produce the bug.
        ctx.Api.GetSlow("/api/heroes", new[] { Fixtures.Hero("hero-other", NotMine, ownerId: "player-2") },
            TimeSpan.FromMilliseconds(400));
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero("hero-mine", Mine) });
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        Open(ctx, "http://localhost/heroes?mine=1");

        var cut = ctx.Render<Heroes>();
        cut.InvokeAsync(() => ctx.SignIn());   // while the ALL read is still out

        cut.WaitForAssertion(() => Assert.Contains(Mine, cut.Markup), TimeSpan.FromSeconds(10));

        // Now let the older ALL response arrive, and hold the page to the newer answer.
        System.Threading.Thread.Sleep(700);

        Assert.Contains(Mine, cut.Markup);
        Assert.DoesNotContain(NotMine, cut.Markup);
        Assert.DoesNotContain("loading the roster", cut.Markup);
    }

    /// <summary>
    /// The risk the fix itself introduces. Holding the link's intent means holding STATE, and
    /// <see cref="ArkadeHeroes.Web.Wallet.WalletState"/> raises OnChange constantly — a balance refresh,
    /// a VTXO landing. An intent that is not spent once honoured would re-apply on every one of them and
    /// drag the tab back under the player's cursor, which is a worse page than the one being fixed.
    /// </summary>
    [Fact]
    public void OnceHonoured_TheLinkIntentDoesNotKeepDraggingTheTabBack()
    {
        using var ctx = Arena();
        Open(ctx, "http://localhost/heroes?mine=1");

        var cut = ctx.Render<Heroes>();
        cut.InvokeAsync(() => ctx.SignIn());
        cut.WaitForAssertion(() => Assert.Contains(Mine, cut.Markup), TimeSpan.FromSeconds(10));

        // The player asks for the whole-game roster instead.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "All").Click();
        cut.WaitForAssertion(() => Assert.Contains(NotMine, cut.Markup));

        // …and the wallet does something ordinary.
        cut.InvokeAsync(() => ctx.State.UpdateBalance(5_000));

        cut.WaitForAssertion(() => Assert.Contains(NotMine, cut.Markup));
        Assert.DoesNotContain(Mine, cut.Markup);
    }
}
