using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>"Pay &amp; breed" sends two payments; the row above it quoted only the one the payer chose.</summary>
public class StudBreedFeeRowTests
{
    private const string Mine = "h-mine";
    private const string Theirs = "h-theirs";

    private static PageTestContext Outgoing(long? breedFeeSats, string status = "accepted")
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
            new StudProposalDto("p-1", "player-1", "player-2", Mine, Theirs, 1_500, status, null, breedFeeSats),
        });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Obsidian Reaver Nyx", ownerId: "player-2"));
        return ctx;
    }

    /// <summary>The line holding the button. TextContent, not Markup — the label is <c>&amp;amp;</c> there,
    /// so a substring search over the raw markup silently matches nothing.</summary>
    private static string PayRow(IRenderedComponent<Breed> cut)
    {
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("div.status-line"), e => e.TextContent.Contains("Pay & breed")));
        return cut.FindAll("div.status-line").Single(e => e.TextContent.Contains("Pay & breed")).TextContent;
    }

    [Fact]
    public void TheRowThatSpendsQuotesTheTreasuryFeeAndNotOnlyTheStudFee()
    {
        using var ctx = Outgoing(8_000);
        var row = PayRow(ctx.Render<Breed>()).Replace(",", "");

        Assert.Contains("8000", row);
        Assert.Contains("1500", row);
    }

    [Fact]
    public void AnUnpricedProposalQuotesNoBreedFeeRatherThanZero()
    {
        // A server that never sends the field must leave the line silent. "0 sats breeding fee" on a breed
        // that bills 1,000 is worse than saying nothing, because the player believes it.
        using var ctx = Outgoing(null);

        Assert.DoesNotContain("breeding fee", PayRow(ctx.Render<Breed>()));
    }
}
