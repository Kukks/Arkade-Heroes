namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// The two ways a page that renders perfectly can still be lying: it can show the wrong number, or it can
/// present a failure as an absence.
///
/// <para>Neither is visible to a check that asks whether the page loaded, and both cost the player
/// something real. A wrong fee is quoted before they spend bitcoin. An empty state standing in for a failed
/// read tells them they own nothing, on a page whose whole job is to show them what they own — and the
/// repo has already shipped that one, which is why the bUnit suite guards it in-process. This is the same
/// question asked of the artifact a player downloads, over a real socket.</para>
/// </summary>
[Collection(PlayableAppCollection.Name)]
public class HonestPageTests(PlayableAppFixture app)
{
    /// <summary>
    /// The landing page quotes the breeding fee THIS server charges.
    ///
    /// <para>The fixture runs a non-default fee precisely so this cannot pass by coincidence: a bundle with
    /// the price baked in would print the shipped 1,000 and fail. The value is read back from the API at
    /// runtime rather than written here as a literal, so the test also fails if the two ever disagree —
    /// which is the actual defect, whichever side moved.</para>
    /// </summary>
    [Fact]
    public async Task TheLandingPageQuotesThisServersBreedingFee()
    {
        var info = await app.Api.Chain.InfoAsync();
        Assert.NotNull(info.Config);
        Assert.Equal(PlayableAppFixture.BreedingFeeSats, info.Config!.BreedingFeeSats);

        var session = await app.OpenAsync("/");
        await session.AssertHealthyAsync("/");

        var body = await session.BodyTextAsync();
        // "Arena online" first: the chip that carries the fee is inside the branch that only renders once
        // the server answered, so without this an assertion on the price could fail for a reason that has
        // nothing to do with the price.
        Assert.Contains("Arena online", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{info.Config.BreedingFeeSats:N0} sat", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A roster that could not be read must say so, and must NOT say the arena is empty.
    ///
    /// <para>"No heroes yet" is a claim about the world. Rendered over a failed read it is false, and it is
    /// false in the direction that makes a player think their heroes are gone. The bUnit suite pins this for
    /// the component; this pins it for the page a player actually downloads, with the failure arriving the
    /// way a real one does — as a status code off the wire, not an injected exception.</para>
    /// </summary>
    [Fact]
    public async Task ARosterThatFailedToLoadDoesNotClaimTheArenaIsEmpty()
    {
        var session = await app.OpenAsync("/heroes", breakApi: "**/api/heroes*");
        var body = await session.BodyTextAsync();

        Assert.DoesNotContain("No heroes yet", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("don't have any heroes", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Couldn't reach the arena", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Same question for the market, where the absence is about other people's property rather than the
    /// player's own — "no resting offers" over a failed read sends someone away from a market that is
    /// actually full.
    /// </summary>
    [Fact]
    public async Task AMarketThatFailedToLoadDoesNotClaimThereAreNoOffers()
    {
        var session = await app.OpenAsync("/market", breakApi: "**/api/offers*");
        var body = await session.BodyTextAsync();

        Assert.DoesNotContain("No resting offers", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Couldn't reach the arena", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// With the game server unreachable the landing page has to say the arena is down — not sit on
    /// "Connecting…" forever, and not quote a price it no longer has any basis for.
    ///
    /// <para>The second clause is the one worth the test, and it is a real regression: the fee chip once
    /// rendered <c>?? 0</c> and told a visitor breeding was FREE whenever the config was missing. What keeps
    /// that from coming back is that the whole chip row lives INSIDE the branch that only draws once the
    /// server has answered. So the guard to hold is the chip's position, not its formatting — if it ever
    /// escapes that branch, "breed fee" appears on a page with no config behind it and this fails.</para>
    /// </summary>
    [Fact]
    public async Task AnUnreachableArenaIsReportedAndQuotesNoPriceAtAll()
    {
        var session = await app.OpenAsync("/", breakApi: "**/api/**");
        var body = await session.BodyTextAsync();

        Assert.Contains("Arena unreachable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("breed fee", body, StringComparison.OrdinalIgnoreCase);
    }
}
