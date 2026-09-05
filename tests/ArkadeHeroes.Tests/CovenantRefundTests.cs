using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Timelocked escrow refunds over the InMemory simulation — the same rules the
/// covenant + operator enforce on regtest: only a party, only their own staked
/// amount, only after expiry, never after settlement, never twice. The refund
/// deadline is an absolute time (escrow-open + WagerEscrowRefundAfter) that the
/// guard compares against the clock, so each test picks the window it needs — a
/// far-future window to observe the LOCKED branch, a zero window to observe the
/// UNLOCKED branch — making every assertion deterministic with no wall-clock
/// waiting and no race under parallel load.
/// </summary>
public class CovenantRefundTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CovenantRefundTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // A server whose refund window is exactly `window`, overriding the 24h default
    // for one test. No appsettings entry shadows this key, so UseSetting binds it at
    // host startup: the deadline the guard checks becomes (escrow-open + window).
    private WebApplicationFactory<Program> WithRefundWindow(TimeSpan window) =>
        _factory.WithWebHostBuilder(b => b.UseSetting("Game:WagerEscrowRefundAfter", window.ToString()))
            // These assert exact stake arithmetic; a paid starter claim would move the balances first.
            .WithFreeStarters();

    private static async Task<(ArkadeHeroesClient Alice, ArkadeHeroesClient Bob, OpenMatchResponse Open)> OpenCovenantMatchAsync(
        WebApplicationFactory<Program> factory, string tag)
    {
        var (alice, _) = await factory.RegisterAsync($"R-Alice-{tag}");
        var (bob, _) = await factory.RegisterAsync($"R-Bob-{tag}");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 4_000, "covenant"));
        return (alice, bob, open);
    }

    [Fact]
    public async Task EscrowParamsEndpoint_ExposesTheRebuildableParams()
    {
        var (alice, _, open) = await OpenCovenantMatchAsync(_factory, "params");

        var parameters = await alice.Matches.EscrowAsync(open.MatchId);
        Assert.Equal(open.MatchId, parameters.MatchId);
        Assert.Equal(4_000, parameters.StakeSats);
        Assert.Equal(open.CommitmentHex, parameters.CommitmentHex, ignoreCase: true);
        Assert.Equal(64, parameters.OraclePkHex.Length);
        Assert.NotEqual(parameters.ChallengerAddress, parameters.DefenderAddress);
        Assert.True(parameters.RefundAfterUnixSeconds > DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task EscrowParamsEndpoint_404ForAMatchWithNothingAtStake()
    {
        // The old version asked this of an INVOICE-mode wagered match. There is no such thing now — a
        // wagered match is covenant-only — so the remaining case is a friendly one, which stakes nothing
        // and therefore has no escrow to describe.
        var (alice, _) = await _factory.RegisterAsync("R-Friendly");
        var (bob, _) = await _factory.RegisterAsync("R-Friendly2");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));

        Assert.Null(open.EscrowAddress);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Matches.EscrowAsync(open.MatchId));
    }

    /// <summary>The treasury never holds a stake, so the custodial path is refused rather than quietly
    /// upgraded — a caller that asked for it is told it is gone.</summary>
    [Fact]
    public async Task AWageredMatchCannotBeOpenedInTheCustodialMode()
    {
        var (alice, _) = await _factory.RegisterAsync("R-NoCustody");
        var (bob, _) = await _factory.RegisterAsync("R-NoCustody2");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000, "invoice")));
        Assert.Contains("never holds your stake", refused.Message);
    }

    [Fact]
    public async Task AbandonedStake_BeforeExpiry_LockedForStaker_RefusedForOthers()
    {
        // Far-future window: the deadline never passes during the test, so the LOCKED
        // branch is observed deterministically — the setup cost can't race the window.
        using var factory = WithRefundWindow(TimeSpan.FromHours(1));
        var (alice, bob, open) = await OpenCovenantMatchAsync(factory, "locked");

        // The challenger stakes; the defender vanishes without staking.
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        // Before expiry the staker is locked out (the FORFEIT_CLOSURE_LOCKED analogue).
        var early = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("locked until", early.Message);

        // A non-party can never trigger anything (refused before the deadline check).
        var (mallory, _) = await factory.RegisterAsync("R-Mallory-locked");
        var outsider = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => mallory.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("Not a party", outsider.Message);

        // The defender never staked — nothing for THEM to refund (also window-independent).
        var unstaked = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => bob.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("Nothing staked", unstaked.Message);
    }

    [Fact]
    public async Task AbandonedStake_AfterExpiry_ReclaimsExactStake_OnceOnly()
    {
        // Zero window: the deadline is immediately past, so the UNLOCKED branch is
        // observed deterministically — no waiting for wall-clock time to advance.
        using var factory = WithRefundWindow(TimeSpan.Zero);
        var (alice, bob, open) = await OpenCovenantMatchAsync(factory, "reclaim");
        var start = InMemoryChainService.FaucetSats;

        // The challenger stakes; the defender vanishes without staking.
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var staked = await alice.Players.MeAsync();
        Assert.Equal(start - 4_000, staked.BalanceSats);

        // A non-party can never trigger anything.
        var (mallory, _) = await factory.RegisterAsync("R-Mallory-reclaim");
        var outsider = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => mallory.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("Not a party", outsider.Message);

        // The defender never staked — nothing for THEM to refund.
        var unstaked = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => bob.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("Nothing staked", unstaked.Message);

        // After expiry the staker reclaims exactly their stake…
        await alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId });
        var refunded = await alice.Players.MeAsync();
        Assert.Equal(start, refunded.BalanceSats);

        // …and only once.
        var again = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("Nothing staked", again.Message);
    }

    [Fact]
    public async Task SettledEscrow_RefusesRefunds()
    {
        // Zero window → immediately past expiry; settlement must STILL win over an
        // expired refund window (the guard checks 'settled' before the deadline).
        using var factory = WithRefundWindow(TimeSpan.Zero);
        var (alice, bob, open) = await OpenCovenantMatchAsync(factory, "settled");
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        await alice.Matches.FightAsync(open.MatchId, new FightRequest("n"));

        var refund = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("already settled", refund.Message);
    }

    [Fact]
    public async Task RefundedMatch_IsExpiredAndDropped_FromTheOpenList()
    {
        // Zero window → Alice can reclaim immediately; the open→expired transition is
        // driven by per-party FUNDING (challenger stake present, then gone), not by
        // waiting for wall-clock time, so no delay is needed.
        using var factory = WithRefundWindow(TimeSpan.Zero);
        var (alice, _, open) = await OpenCovenantMatchAsync(factory, "expire");
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        // Bob never accepts/stakes; before reclaim the open match is live and listed
        // (the challenger IS funded, so it isn't abandoned even past the window).
        var listedBefore = await alice.Matches.ListAsync("open");
        Assert.Contains(listedBefore, m => m.MatchId == open.MatchId);

        // Alice reclaims her stake — the challenger escrow is now empty.
        await alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId });

        // Listing reconciles per-party funding: the abandoned (refunded) match is
        // marked 'expired' and drops out of the open list.
        var listedAfter = await alice.Matches.ListAsync("open");
        Assert.DoesNotContain(listedAfter, m => m.MatchId == open.MatchId);
        var expired = await alice.Matches.ListAsync("expired");
        Assert.Contains(expired, m => m.MatchId == open.MatchId);
    }
}
