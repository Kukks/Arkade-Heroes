using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>The daily faucet governor: sats are real BTC, so the treasury can't be overdrawn. A claim pays
/// only what the treasury can afford (down to zero) instead of throwing — the insolvency guard. Fresh factory
/// per test so the treasury starts empty (the pot + faucet are global singleton state).</summary>
public class DailyFaucetGovernorTests
{
    [Fact]
    public async Task Claim_OnEmptyTreasury_PaysZero_DoesNotThrow_StreakStillAdvances()
    {
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen().WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Daily-Gov");
        await alice.ClaimStartersAsync();

        // A fresh InMemory treasury is empty; the base daily reward isn't coverable — but the claim must
        // degrade gracefully, not fault.
        var claim = await alice.Daily.ClaimAsync();

        Assert.Equal(0, claim.AwardedSats);   // capped to the empty treasury
        Assert.Equal(1, claim.Streak);        // the player still "showed up"
        Assert.True((await alice.Daily.StatusAsync()).ClaimedToday);
    }

    [Fact]
    public async Task Claim_CapsToPartialTreasury()
    {
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen().WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Daily-Gov-Partial");
        await alice.ClaimStartersAsync();
        await alice.Dev.FundTreasuryAsync(new { Sats = 20L });   // less than the 50-sat base reward

        var claim = await alice.Daily.ClaimAsync();

        Assert.Equal(20, claim.AwardedSats);   // capped to exactly what the treasury holds
    }
}
