using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Timelocked escrow refunds over the InMemory simulation — the same rules the
/// covenant + operator enforce on regtest: only a party, only their own staked
/// amount, only after expiry, never after settlement, never twice. Uses its
/// own factory with a 1-second refund window (env var — WebApplicationFactory
/// config overrides lose to appsettings).
/// </summary>
public class CovenantRefundTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public CovenantRefundTests()
    {
        Environment.SetEnvironmentVariable("Game__WagerEscrowRefundAfter", "00:00:01");
        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Game__WagerEscrowRefundAfter", null);
        _factory.Dispose();
    }

    private async Task<(ArkadeHeroesClient Alice, ArkadeHeroesClient Bob, OpenMatchResponse Open)> OpenCovenantMatchAsync(string tag)
    {
        var (alice, _) = await _factory.RegisterAsync($"R-Alice-{tag}");
        var (bob, _) = await _factory.RegisterAsync($"R-Bob-{tag}");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 4_000, "covenant"));
        return (alice, bob, open);
    }

    [Fact]
    public async Task EscrowParamsEndpoint_ExposesTheRebuildableParams()
    {
        var (alice, _, open) = await OpenCovenantMatchAsync("params");

        var parameters = await alice.Matches.EscrowAsync(open.MatchId);
        Assert.Equal(open.MatchId, parameters.MatchId);
        Assert.Equal(4_000, parameters.StakeSats);
        Assert.Equal(open.CommitmentHex, parameters.CommitmentHex, ignoreCase: true);
        Assert.Equal(64, parameters.OraclePkHex.Length);
        Assert.NotEqual(parameters.ChallengerAddress, parameters.DefenderAddress);
        Assert.True(parameters.RefundAfterUnixSeconds > DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task EscrowParamsEndpoint_404ForInvoiceModeMatches()
    {
        var (alice, _) = await _factory.RegisterAsync("R-Invoice");
        var (bob, _) = await _factory.RegisterAsync("R-Invoice2");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000, "invoice"));

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Matches.EscrowAsync(open.MatchId));
    }

    [Fact]
    public async Task AbandonedStake_RefundableAfterExpiry_OnceOnly_PartiesOnly()
    {
        var (alice, bob, open) = await OpenCovenantMatchAsync("lifecycle");
        var start = InMemoryChainService.FaucetSats;

        // The challenger stakes; the defender vanishes without staking.
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var staked = await alice.Players.MeAsync();
        Assert.Equal(start - 4_000, staked.BalanceSats);

        // Before expiry: locked (the FORFEIT_CLOSURE_LOCKED analogue).
        var early = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("locked until", early.Message);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // A non-party can never trigger anything.
        var (mallory, _) = await _factory.RegisterAsync("R-Mallory");
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
        var (alice, bob, open) = await OpenCovenantMatchAsync("settled");
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        await alice.Matches.FightAsync(open.MatchId, new FightRequest("n"));

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // past expiry — settlement must still win
        var refund = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId }));
        Assert.Contains("already settled", refund.Message);
    }

    [Fact]
    public async Task RefundedMatch_IsExpiredAndDropped_FromTheOpenList()
    {
        var (alice, _, open) = await OpenCovenantMatchAsync("expire");
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        // Bob never accepts/stakes; before expiry the open match is live and listed
        // (the challenger IS funded, so it isn't abandoned even past the window).
        var listedBefore = await alice.Matches.ListAsync("open");
        Assert.Contains(listedBefore, m => m.MatchId == open.MatchId);

        // Past expiry, Alice reclaims her stake — the challenger escrow is now empty.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await alice.Dev.RefundEscrowAsync(new { MatchId = open.MatchId });

        // Listing reconciles per-party funding: the abandoned (refunded) match is
        // marked 'expired' and drops out of the open list.
        var listedAfter = await alice.Matches.ListAsync("open");
        Assert.DoesNotContain(listedAfter, m => m.MatchId == open.MatchId);
        var expired = await alice.Matches.ListAsync("expired");
        Assert.Contains(expired, m => m.MatchId == open.MatchId);
    }
}
