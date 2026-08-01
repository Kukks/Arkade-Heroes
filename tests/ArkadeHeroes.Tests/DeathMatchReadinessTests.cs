using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The readiness signal a settling client polls instead of attempting.
///
/// <para>Staking into a death-match escrow is a real spend that takes time to settle into arkd's indexer,
/// so the browser has to wait for something it cannot observe directly. Its only way to find out used to
/// be to POST the settle and read the refusal — and a happy-path run therefore ended with FOURTEEN
/// <c>POST /api/deathmatch/{id}/settle → 400</c> console errors behind it. Chromium logs a console error
/// for every failed fetch, so a working flow was indistinguishable from a broken one, which is how people
/// learn to stop reading the console. It is also a test-suite liability now: the browser walk asserts zero
/// console errors on every page.</para>
///
/// <para>These pin the two halves that matter. The readiness read must track the SETTLE's own gates — a
/// signal that says "go" early just moves the 400 — and it must move nothing while it looks, because the
/// caller is polling it on a timer.</para>
/// </summary>
public class DeathMatchReadinessTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public DeathMatchReadinessTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ReadinessTracksTheSettlesOwnGates_StakeByStakeAndFeeByFee()
    {
        var (alice, _) = await _factory.RegisterAsync("DMR-A");
        var (bob, _) = await _factory.RegisterAsync("DMR-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero));
        var accepted = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);

        // Nothing staked, nothing paid.
        var cold = await alice.DeathMatch.ReadinessAsync(open.DeathMatchId);
        Assert.False(cold.StakesFunded);
        Assert.False(cold.Ready);
        Assert.False(cold.Completed);

        // One side staked is not both sides staked.
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        Assert.False((await alice.DeathMatch.ReadinessAsync(open.DeathMatchId)).Ready);

        // Both staked, but the fees are the second gate — this is the state that produced the 400s.
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        var staked = await alice.DeathMatch.ReadinessAsync(open.DeathMatchId);
        Assert.True(staked.StakesFunded);
        Assert.False(staked.ChallengerFeePaid);
        Assert.False(staked.Ready);

        await alice.PayInvoiceAsync(open.FeeInvoice!.InvoiceId);
        var half = await alice.DeathMatch.ReadinessAsync(open.DeathMatchId);
        Assert.True(half.ChallengerFeePaid);
        Assert.False(half.DefenderFeePaid);
        Assert.False(half.Ready);

        await bob.PayInvoiceAsync(accepted.FeeInvoice!.InvoiceId);
        var ready = await alice.DeathMatch.ReadinessAsync(open.DeathMatchId);
        Assert.True(ready.Ready);

        // The contract this whole endpoint rests on: once it says go, the settle goes — no 400 in between.
        var settled = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("dmr-nonce"));
        Assert.NotNull(settled.Result);

        // And afterwards it says "stop", not "wait" — a client that could not tell those apart would poll
        // a resolved death-match forever.
        var after = await alice.DeathMatch.ReadinessAsync(open.DeathMatchId);
        Assert.True(after.Completed);
        Assert.False(after.Ready);
    }

    /// <summary>
    /// Polling must not resolve anything. The caller reads this on a timer, so a readiness check with a
    /// side effect would be a settle nobody asked for — and this one burns a hero.
    /// </summary>
    [Fact]
    public async Task ReadingReadinessRepeatedlyNeverSettlesTheMatch()
    {
        var (alice, _) = await _factory.RegisterAsync("DMR-Idem-A");
        var (bob, _) = await _factory.RegisterAsync("DMR-Idem-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero));
        var accepted = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await alice.PayInvoiceAsync(open.FeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accepted.FeeInvoice!.InvoiceId);

        for (var i = 0; i < 5; i++)
            Assert.True((await alice.DeathMatch.ReadinessAsync(open.DeathMatchId)).Ready);

        // Still unresolved, and both heroes still alive after all that looking.
        var listed = (await alice.DeathMatch.ListAsync()).Single(d => d.DeathMatchId == open.DeathMatchId);
        Assert.Equal("accepted", listed.Status);
        Assert.NotNull(await alice.Heroes.GetAsync(aliceHero));
        Assert.NotNull(await bob.Heroes.GetAsync(bobHero));
    }

    /// <summary>Only a participant may look — the same rule the settle enforces, so an onlooker cannot
    /// use this to watch someone else's escrow fill up.</summary>
    [Fact]
    public async Task AStrangerCannotReadIt()
    {
        var (alice, _) = await _factory.RegisterAsync("DMR-Priv-A");
        var (bob, _) = await _factory.RegisterAsync("DMR-Priv-B");
        var (mallory, _) = await _factory.RegisterAsync("DMR-Priv-M");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero));

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => mallory.DeathMatch.ReadinessAsync(open.DeathMatchId));
        Assert.Contains("participant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
