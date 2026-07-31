using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// Put real state on the server, then make the browser show it.
///
/// <para>Rendering an empty page is the easy half. Every "does this route work" check in this repo —
/// including the walk next door — runs against a server that holds nothing, so a page passes by drawing its
/// empty state, and the wire between the server and the DOM is never crossed under load. A hero that exists
/// is what exercises the DTO, the JSON contract, the list projection and the component that draws it.</para>
///
/// <para>Each assertion here names a value the SERVER chose — a hero's generated name, an ask price, a
/// treasury total — and requires the browser to print that exact value. A test that asserted "some hero is
/// listed" would survive the roster rendering the wrong hero, and a test that asserted a literal price
/// would survive the page hardcoding it.</para>
/// </summary>
[Collection(PlayableAppCollection.Name)]
public class SeededArenaTests(PlayableAppFixture app)
{
    /// <summary>Registers a player and buys them one hero through the real paid path — quote, pay, claim.</summary>
    private async Task<(ArkadeHeroesClient Client, HeroDto Hero)> SeedHeroAsync(string name)
    {
        var client = app.Api;
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, $"sim-wallet-{Guid.NewGuid():N}"));

        var quote = await client.Heroes.RequestStartersAsync();
        if (quote.Fee is { } fee) await client.Dev.PayInvoiceAsync(new { InvoiceId = fee.InvoiceId });
        var claimed = await client.Heroes.ClaimStartersAsync();

        return (client, claimed.Heroes[0]);
    }

    /// <summary>
    /// The roster draws a hero the server actually minted, by the name the server gave it.
    ///
    /// <para>Signed out on purpose. The "All" view is the public one, and it is the view a stranger who
    /// followed a link sees first — so it is the one where a broken list projection reaches the most people
    /// and the one no existing test renders against real data.</para>
    /// </summary>
    [Fact]
    public async Task TheRosterDrawsAHeroTheServerMinted()
    {
        var (_, hero) = await SeedHeroAsync("Roster Walker");

        var session = await app.OpenAsync("/heroes");
        await session.AssertHealthyAsync("/heroes with a seeded hero");

        var body = await session.BodyTextAsync();
        Assert.Contains(hero.Name, body, StringComparison.Ordinal);
        // The empty state and a populated roster are both "a page that rendered"; only this separates them.
        Assert.DoesNotContain("No heroes yet", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A hero's own page, reached by the id the server issued — the deep link a player bookmarks or is sent.
    ///
    /// <para>Two things have to hold at once for this to pass, and they fail independently: the SPA fallback
    /// has to serve the bundle for a path with no file behind it, and the page has to resolve the id against
    /// the API once it boots. A 404 and a page that loads but cannot find the hero look the same to a user
    /// and completely different to fix.</para>
    /// </summary>
    [Fact]
    public async Task AHerosOwnPageResolvesTheIdFromTheUrl()
    {
        var (_, hero) = await SeedHeroAsync("Deep Link Owner");

        var session = await app.OpenAsync($"/heroes/{hero.Id}");
        await session.AssertHealthyAsync($"/heroes/{hero.Id}");

        Assert.Contains(hero.Name, await session.BodyTextAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A resting offer shows up in the market at the price the seller asked.
    ///
    /// <para>The ask is the assertion. A market that lists the right hero at the wrong number is worse than
    /// one that lists nothing, and it is real money — so the figure on screen is compared against what the
    /// API says the offer is for, not against a constant this test made up.</para>
    /// </summary>
    [Fact]
    public async Task AnOfferAppearsInTheMarketAtItsAskingPrice()
    {
        var (seller, hero) = await SeedHeroAsync("Market Seller");

        const long ask = 24_680;      // deliberately not a round default: a hardcoded price cannot match it
        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();   // reconcile: the deposit is what makes a listing active

        var listed = (await seller.Offers.ListAsync()).Single(o => o.OfferId == offer.OfferId);

        var session = await app.OpenAsync("/market");
        await session.AssertHealthyAsync("/market with a live offer");

        var body = await session.BodyTextAsync();
        Assert.Contains(listed.AskSats.ToString("N0"), body, StringComparison.Ordinal);
        Assert.DoesNotContain("No resting offers", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The treasury figure on the ranks page is the treasury figure the server holds.
    ///
    /// <para>Sats are real bitcoin here and the treasury cannot print them, so a number that drifts from the
    /// server's is not a display bug — it is the page most likely to be read as reassurance being wrong
    /// about solvency. Seeding a claim first is what makes this non-trivial: against an untouched server the
    /// balance is zero, and zero is also what a page that never read anything would show.</para>
    ///
    /// <para>Read out of the Treasury card SPECIFICALLY, not out of the page. This assertion was written
    /// against the whole body first and it was worthless: on a lightly-seeded server the balance, the total
    /// inflow and the single inflow row are all the same number, so corrupting the balance left two other
    /// copies of the right one on screen and the test stayed green. Proven by breaking it.</para>
    /// </summary>
    [Fact]
    public async Task TheTreasuryFigureMatchesTheServersOwnNumber()
    {
        var client = app.Api;
        await SeedHeroAsync("Treasury Filler");   // the claim fee is treasury income

        var health = await client.Economy.HealthAsync();
        Assert.True(health.TreasuryBalanceSats > 0,
            "seeding was supposed to leave fee income in the treasury; with zero this test proves nothing");

        var session = await app.OpenAsync("/leaderboard");
        await session.AssertHealthyAsync("/leaderboard");

        var shown = await session.Page.Locator(".card:has(h3:text-is('Treasury')) .chip.mono").First.InnerTextAsync();
        Assert.Equal($"{health.TreasuryBalanceSats:N0} sat", shown.Trim());
    }
}
