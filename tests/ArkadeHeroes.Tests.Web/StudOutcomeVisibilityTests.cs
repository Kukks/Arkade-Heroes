using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>A stud proposal is a request another player ANSWERS. "declined" and "refunded" were filtered
/// out of the proposer's list, so a refusal and a proposal never sent looked identical.</summary>
public class StudOutcomeVisibilityTests
{
    private const string Mine = "h-mine";
    private const string Theirs = "h-theirs";

    private static PageTestContext WithOutgoing(string status)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[]
        {
            Fixtures.Hero(Mine, "Crimson Vanguard Vale"),
            Fixtures.Hero("h-spare", "Azure Warden Rook"),
        });
        ctx.Api.Get("/api/players/me", Fixtures.Player());
        ctx.Api.Get("/api/stud", new[]
        {
            new StudProposalDto("p-1", "player-1", "player-2", Mine, Theirs, 1_500, status, null),
        });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Obsidian Reaver Nyx", ownerId: "player-2"));
        return ctx;
    }

    private static string Proposals(IRenderedComponent<Breed> cut)
    {
        cut.WaitForAssertion(() => Assert.Contains("Your proposals", cut.Markup));
        return cut.Find("div.card:has(h4)").TextContent;
    }

    [Fact]
    public void ADeclinedProposalSaysSo_AndThatNothingWasBilled()
    {
        using var ctx = WithOutgoing("declined");
        var text = Proposals(ctx.Render<Breed>());

        Assert.Contains("declined", text);
        Assert.Contains("nothing was billed", text);
    }

    [Fact]
    public void ARefundedProposalSaysWhereTheFeesWent()
    {
        using var ctx = WithOutgoing("refunded");
        var text = Proposals(ctx.Render<Breed>());

        Assert.Contains("refunded", text);
        Assert.Contains("went back to your wallet", text);
    }

    /// <summary>A finished breed needs no row — the foal is in the roster, and the list stays a work queue.</summary>
    [Fact]
    public void ACompletedProposalStaysOutOfTheList()
    {
        using var ctx = WithOutgoing("completed");
        var cut = ctx.Render<Breed>();

        cut.WaitForAssertion(() => Assert.Contains("Stud service", cut.Markup));
        Assert.DoesNotContain("Your proposals", cut.Markup);
    }
}
