using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Wallet;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The browser's own re-prompt decision. <see cref="TermsState.MustAccept"/> is the function that decides
/// whether the acceptance modal goes back in front of a player, and for a SIGNED-IN one it reads exactly
/// one thing: the version the server has on file. The server-side tests pin the predicate; these pin the
/// gate that calls it, which is where a player actually experiences being asked again.
///
/// <para>Both cases are constructed fresh, which is what a page reload gives you: nothing accepted this
/// session, and — for a signed-in player — no local cache consulted at all.</para>
/// </summary>
public class TermsGateDecisionTests
{
    private static TermsState GateFor(PageTestContext ctx) => ctx.Services.GetRequiredService<TermsState>();

    [Fact]
    public void APlayerStillOnVersion1_IsAskedAgain()
    {
        // Version 1's §5 understated what the game charges: it never mentioned the fee to recruit a hero,
        // which is the first thing a new player pays, and it omitted fusion, gauntlet entry, match entry
        // fees and the tournament rake. Correcting that bumped Terms.CurrentVersion, and this is what the
        // bump is FOR — the modal comes back. Fails if the version is ever walked back to 1.
        using var ctx = new PageTestContext();
        var player = Fixtures.Player() with { TermsAcceptedVersion = 1 };

        Assert.True(GateFor(ctx).MustAccept(player),
            "a player whose acceptance predates the corrected fee disclosure must be asked again");
    }

    [Fact]
    public void APlayerOnTodaysVersion_IsNotAskedAgainOnReload()
    {
        // The other half, and the one a careless bump breaks: re-asking must STOP once they have answered.
        // The server's record has to be enough on its own here — this state has no session answer and no
        // cache behind it, exactly like the first render after a refresh.
        using var ctx = new PageTestContext();
        var player = Fixtures.Player() with { TermsAcceptedVersion = Terms.CurrentVersion };

        Assert.False(GateFor(ctx).MustAccept(player),
            "an acceptance of the current version must not re-prompt after a reload");
    }
}
