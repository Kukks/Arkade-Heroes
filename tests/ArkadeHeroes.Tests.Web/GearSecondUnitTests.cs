using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Gear is allocated PER HERO: one unit arms exactly one of them. The shop replaced Buy with an
/// "owned ✓" chip the moment a player held any, so a second unit was unbuyable from the browser — while
/// the equip refusal told the player to go and buy one. Every player with two heroes hit it.
/// </summary>
public class GearSecondUnitTests
{
    private static ItemDto Blade() => new(
        Id: "rusty-blade", Name: "Rusty Blade", Slot: "Weapon",
        MaxHp: 0, Attack: 3, Magic: 0, Defense: 0, Speed: 0, CritPercent: 0,
        PriceSats: 500);

    private static PageTestContext Shop(long unitsHeld)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/items", new[] { Blade() });
        ctx.Api.Get("/api/items/mine",
            unitsHeld > 0 ? new Dictionary<string, long> { ["rusty-blade"] = unitsHeld } : new());
        return ctx;
    }

    private static bool HasBuyButton(IRenderedComponent<Gear> cut) =>
        cut.FindAll("button").Any(b => b.TextContent.Contains("Buy", StringComparison.Ordinal));

    [Fact]
    public void OwningOneUnitDoesNotHideTheWayToBuyASecond()
    {
        using var ctx = Shop(unitsHeld: 1);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("Rusty Blade", cut.Markup));
        Assert.True(HasBuyButton(cut), "a second unit is a real purchase — it must stay reachable");
        Assert.Contains("owned ×1", cut.Markup);
    }

    [Fact]
    public void TheCountIsShown_NotJustThatSomethingIsOwned()
    {
        // "owned" alone cannot answer the question the player actually has: can I arm another hero?
        using var ctx = Shop(unitsHeld: 3);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("owned ×3", cut.Markup));
    }

    [Fact]
    public void AnUnownedItemStillReadsAsAPlainBuy()
    {
        using var ctx = Shop(unitsHeld: 0);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("Rusty Blade", cut.Markup));
        Assert.True(HasBuyButton(cut));
        Assert.DoesNotContain("owned", cut.Markup);
    }
}
