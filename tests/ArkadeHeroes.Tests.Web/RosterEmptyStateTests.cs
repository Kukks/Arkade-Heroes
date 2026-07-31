using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// REGRESSION 2 — an empty roster state that says "you have no heroes" when the load merely FAILED.
///
/// <para>"You own nothing" and "we could not read what you own" are different facts with different
/// remedies, and every one of these pages used to state the first while meaning either. A player with a
/// full roster hit a server hiccup and was told to go and claim starters — advice that would have cost
/// them sats to follow, for heroes they already had.</para>
///
/// <para>The reason this class of bug survives a green unit suite is that the failing read is SWALLOWED:
/// the page catches, leaves its list empty, and renders the empty-state branch. Nothing throws, nothing
/// logs, and the only observable difference is the sentence on the screen — which is why it takes a
/// renderer to catch it.</para>
/// </summary>
public class RosterEmptyStateTests
{
    // ── /heroes ──────────────────────────────────────────────────────────────

    [Fact]
    public void Heroes_WhenTheRosterReadFails_SaysSoInsteadOfClaimingYouOwnNothing()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.GetFails("/api/heroes");

        var cut = ctx.Render<Heroes>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("loading the roster", cut.Markup));

        Assert.Contains("Couldn't reach the arena", cut.Markup);
        // The lie, in every wording the page can produce.
        Assert.DoesNotContain("No heroes yet", cut.Markup);
        Assert.DoesNotContain("No heroes minted yet", cut.Markup);
        Assert.DoesNotContain("You don't have any heroes yet", cut.Markup);
    }

    /// <summary>A genuinely empty roster still has to read as empty — the fix must not turn every empty
    /// state into an error.</summary>
    [Fact]
    public void Heroes_WhenTheRosterIsGenuinelyEmpty_SaysThat()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes", Array.Empty<ArkadeHeroes.Shared.HeroDto>());

        var cut = ctx.Render<Heroes>();

        cut.WaitForAssertion(() => Assert.Contains("No heroes", cut.Markup));
        Assert.DoesNotContain("Couldn't reach the arena", cut.Markup);
    }

    // ── /deathmatch ──────────────────────────────────────────────────────────

    /// <summary>
    /// The same lie on the page where following it is most expensive: death-match sends the player to buy
    /// starters, and a recruit is a real spend.
    /// </summary>
    [Fact]
    public void DeathMatch_WhenTheRosterReadFails_SaysSoInsteadOfSendingYouToBuyHeroes()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.GetFails("/api/heroes/mine");

        var cut = ctx.Render<DeathMatch>();

        // The page retries with a bounded backoff before it gives up, so allow for that here.
        cut.WaitForAssertion(
            () => Assert.Contains("Couldn't read your roster", cut.Markup),
            TimeSpan.FromSeconds(15));

        Assert.DoesNotContain("You need a hero first", cut.Markup);
        Assert.DoesNotContain("claim your starters", cut.Markup);
    }

    [Fact]
    public void DeathMatch_WhenTheRosterIsGenuinelyEmpty_SendsYouToRecruit()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/heroes/mine", Array.Empty<ArkadeHeroes.Shared.HeroDto>());
        ctx.Api.Get("/api/deathmatch", Array.Empty<ArkadeHeroes.Shared.DeathMatchDto>());

        var cut = ctx.Render<DeathMatch>();

        cut.WaitForAssertion(() => Assert.Contains("You need a hero first", cut.Markup));
        Assert.DoesNotContain("Couldn't read your roster", cut.Markup);
    }
}
