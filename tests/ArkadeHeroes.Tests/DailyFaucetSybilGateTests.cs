using ArkadeHeroes.Client.Sdk;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The two gates standing between the daily faucet and a farm.
///
/// <para>Sats are real bitcoin out of a treasury that cannot inflate to cover them, and registration
/// costs an attacker nothing: the signed challenge only proves you hold a key you invented, and keys are
/// free. So the faucet ships CLOSED, and even when an operator opens it, it pays only accounts that own a
/// hero — which raises the price of a farmed account from "generate a key" to "also claim starters".</para>
///
/// <para>Neither gate is the whole answer to sybil (see the starter-claim gate), and the treasury reserve
/// floor is a backstop rather than a fix. These tests pin the part that is decided.</para>
/// </summary>
public class DailyFaucetSybilGateTests
{
    [Fact]
    public async Task ShippedDefaults_RefuseTheClaimOutright()
    {
        // No WithDailyFaucetOpen: this is the configuration an operator gets if they change nothing.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Sybil-Default");
        await alice.ClaimStartersAsync();
        await alice.Dev.FundTreasuryAsync(new { Sats = 50_000L });

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Daily.ClaimAsync());
        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShippedDefaults_PayNothing_EvenAcrossManyFreshAccounts()
    {
        // The actual attack, in miniature: a funded treasury and a pile of free identities. The point is
        // not that each claim throws — it is that the treasury is untouched at the end.
        // Claims are free here on purpose: the assertion is that the treasury does not MOVE, and a paid
        // claim would move it upward — the opposite of the leak under test, but still a broken equality.
        using var factory = new WebApplicationFactory<Program>().WithFreeStarters();
        var (funder, _) = await factory.RegisterAsync("Sybil-Funder");
        await funder.Dev.FundTreasuryAsync(new { Sats = 50_000L });
        var before = (await funder.Economy.HealthAsync()).TreasuryBalanceSats;

        for (var i = 0; i < 8; i++)
        {
            var (farmed, _) = await factory.RegisterAsync($"Sybil-Farm-{i}");
            await farmed.ClaimStartersAsync();
            await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => farmed.Daily.ClaimAsync());
        }

        Assert.Equal(before, (await funder.Economy.HealthAsync()).TreasuryBalanceSats);
    }

    [Fact]
    public async Task TheServerPublishesWhetherTheFaucetIsOpen_SoTheClientCanHideIt()
    {
        // The frontend renders the daily surface off this flag alone. If it stopped being published, the
        // card would silently vanish on every server — a feature disappearing quietly is exactly the kind
        // of thing no other test here would catch.
        using var closed = new WebApplicationFactory<Program>();
        var (a, _) = await closed.RegisterAsync("Publish-Closed");
        Assert.False((await a.Chain.InfoAsync()).Config?.DailyRewardEnabled);

        using var open = new WebApplicationFactory<Program>().WithDailyFaucetOpen();
        var (b, _) = await open.RegisterAsync("Publish-Open");
        Assert.True((await b.Chain.InfoAsync()).Config?.DailyRewardEnabled);
    }

    [Fact]
    public async Task AnOpenFaucet_StillRefusesAWalletThatOwnsNoHero()
    {
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen();
        var (drifter, _) = await factory.RegisterAsync("Sybil-NoHeroes");
        await drifter.Dev.FundTreasuryAsync(new { Sats = 50_000L });

        // Registered, funded treasury, faucet open — and still refused, because it owns nothing.
        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => drifter.Daily.ClaimAsync());
        Assert.Contains("starter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnOpenFaucet_PaysOnceTheSameWalletClaimsItsStarters()
    {
        // The positive control for the test above: same wallet, same server, one thing changed. Without
        // this, "refused" could be coming from anywhere and the gate above would prove nothing.
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen();
        var (alice, _) = await factory.RegisterAsync("Sybil-ThenHeroes");
        await alice.Dev.FundTreasuryAsync(new { Sats = 50_000L });

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Daily.ClaimAsync());

        await alice.ClaimStartersAsync();
        var claim = await alice.Daily.ClaimAsync();
        Assert.True(claim.AwardedSats > 0, "an open faucet must pay a player who owns a hero");
    }

    [Fact]
    public async Task TheRefusalCostsThePlayerNothing_TheDayIsStillClaimable()
    {
        // A refusal must not consume the day. The claim path deliberately consumes the day BEFORE paying
        // (so a crash mid-payout cannot be re-claimed), and both gates sit in front of that write — if one
        // of them ever moved below it, a rejected claim would silently burn the player's day.
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen();
        var (alice, _) = await factory.RegisterAsync("Sybil-DayIntact");
        await alice.Dev.FundTreasuryAsync(new { Sats = 50_000L });

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Daily.ClaimAsync());
        Assert.False((await alice.Daily.StatusAsync()).ClaimedToday);

        await alice.ClaimStartersAsync();
        Assert.True((await alice.Daily.ClaimAsync()).AwardedSats > 0);
    }
}
