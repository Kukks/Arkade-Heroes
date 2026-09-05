using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The stud consent inbox asks a player to breed one of their heroes with someone else's. The
/// counterparty's hero is by definition not in <c>_mine</c>, and the row resolved names against that list
/// alone — so the one hero the decision is actually about rendered as a raw id, on the row carrying the
/// Accept button.
/// </summary>
public class StudRowNameTests
{
    private const string Mine = "h-mine";
    private const string Theirs = "h-theirs";

    private static PageTestContext Inbox()
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
            new StudProposalDto("p-1", "player-2", "player-1", Theirs, Mine, 1_500, "proposed", null),
        });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Obsidian Reaver Nyx", ownerId: "player-2"));
        return ctx;
    }

    [Fact]
    public void TheHeroYouAreBeingAskedToBreedWithIsNamed()
    {
        using var ctx = Inbox();
        var cut = ctx.Render<Breed>();

        cut.WaitForAssertion(() => Assert.Contains("Obsidian Reaver Nyx", cut.Markup));
        Assert.DoesNotContain(Theirs, cut.Markup);
    }

    [Fact]
    public void AnUnreadableCounterpartyStillLeavesTheRowUsable()
    {
        // Best-effort: one hero that will not load must not cost the player their whole consent inbox.
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[]
        {
            Fixtures.Hero(Mine, "Crimson Vanguard Vale"),
            Fixtures.Hero("h-spare", "Azure Warden Rook"),   // the page needs two fertile heroes to render at all
        });
        ctx.Api.Get("/api/players/me", Fixtures.Player());
        ctx.Api.Get("/api/stud", new[]
        {
            new StudProposalDto("p-1", "player-2", "player-1", Theirs, Mine, 1_500, "proposed", null),
        });
        ctx.Api.GetFails($"/api/heroes/{Theirs}");

        using (ctx)
        {
            var cut = ctx.Render<Breed>();
            cut.WaitForAssertion(() => Assert.Contains("Asked of your heroes", cut.Markup));
            Assert.Contains("1500", cut.Markup.Replace(",", ""));
        }
    }
}
