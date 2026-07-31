using ArkadeHeroes.Web.Pages;
using Bunit;

// The page and the rules type are both called Gauntlet; this test is about what the PAGE says the RULES
// charge, so both are in play and the rules type gets the alias.
using GauntletRules = ArkadeHeroes.Core.Progression.Gauntlet;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// REGRESSION 4 — a page claiming an action is FREE when it charges a fee.
///
/// <para>/play exists to tell a player what each mode costs before they pick one, and it labelled the
/// Gauntlet "free". The Gauntlet charges <see cref="GauntletRules.Fee"/> — a same-level match fee plus a
/// flat premium — scaled to the hero's level, so the one card whose entire job was to state a price
/// stated the wrong one, and the higher the hero, the bigger the lie.</para>
///
/// <para>These assertions are grounded in the fee FUNCTION rather than a copied number, so they follow
/// the game rather than freezing them. If an operator ever genuinely makes the Gauntlet free, the fee
/// goes to zero and the test asks for the "free" chip instead — the label is pinned to the truth, not to
/// a string someone once typed.</para>
/// </summary>
public class StakeLabelTests
{
    /// <summary>The card for a mode, as rendered — icon, name, stake chip and blurb.</summary>
    private static string Card(IRenderedComponent<Play> cut, string modeName) =>
        cut.FindAll("a.play-card")
           .Single(e => e.QuerySelector("h4")!.TextContent.Trim() == modeName)
           .InnerHtml;

    [Fact]
    public void Gauntlet_IsNotAdvertisedAsFree_BecauseItCharges()
    {
        // The premise. If this ever stops holding, the assertion below is the wrong one to make.
        Assert.True(GauntletRules.Fee(heroLevel: 1) > 0, "the Gauntlet is expected to charge an entry fee");

        using var ctx = new PageTestContext();
        var cut = ctx.Render<Play>();

        var gauntlet = Card(cut, "Gauntlet");

        Assert.DoesNotContain(">free<", gauntlet);
        Assert.Contains("costs sats", gauntlet);
    }

    /// <summary>
    /// Every mode that charges must be labelled as charging. Pinned as a set so a mode added later cannot
    /// quietly inherit the "free" default — the failure here is the same one the page was built to stop.
    /// </summary>
    [Theory]
    [InlineData("Duel")]
    [InlineData("Squad 3v3")]
    [InlineData("Tournaments")]
    [InlineData("Breed")]
    public void ModesThatChargeSatsSaySo(string mode)
    {
        using var ctx = new PageTestContext();
        var cut = ctx.Render<Play>();

        Assert.DoesNotContain(">free<", Card(cut, mode));
    }

    /// <summary>
    /// And the honest direction: Spar and Trials really are free, and mislabelling them "costs sats"
    /// would push players off the two modes that exist for learning. Both are checked against the server's
    /// own behaviour — Trials opens on a commit with no fee invoice, Spar never leaves the browser.
    /// </summary>
    [Theory]
    [InlineData("Spar")]
    [InlineData("Trials")]
    public void ModesThatAreActuallyFreeSaySo(string mode)
    {
        using var ctx = new PageTestContext();
        var cut = ctx.Render<Play>();

        Assert.Contains(">free<", Card(cut, mode));
    }

    /// <summary>
    /// Death-match is the one where the stake is not sats at all, and calling it either "free" or merely
    /// "costs sats" would understate what is actually being wagered.
    /// </summary>
    [Fact]
    public void DeathMatchIsLabelledPermanent()
    {
        using var ctx = new PageTestContext();
        var cut = ctx.Render<Play>();

        Assert.Contains(">permanent<", Card(cut, "Death-match"));
    }
}
