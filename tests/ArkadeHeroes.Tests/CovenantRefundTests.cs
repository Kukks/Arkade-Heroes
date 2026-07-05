using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.Covenants;
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

    private async Task<(HttpClient Alice, HttpClient Bob, OpenMatchResponse Open)> OpenCovenantMatchAsync(string tag)
    {
        var (alice, _) = await _factory.RegisterAsync($"R-Alice-{tag}");
        var (bob, _) = await _factory.RegisterAsync($"R-Bob-{tag}");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 4_000, "covenant")))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        return (alice, bob, open);
    }

    [Fact]
    public async Task EscrowParamsEndpoint_ExposesTheRebuildableParams()
    {
        var (alice, _, open) = await OpenCovenantMatchAsync("params");

        var parameters = (await alice.GetFromJsonAsync<WagerEscrowParams>(
            $"/api/matches/{open.MatchId}/escrow"))!;
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
        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000, "invoice")))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;

        var response = await alice.GetAsync($"/api/matches/{open.MatchId}/escrow");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AbandonedStake_RefundableAfterExpiry_OnceOnly_PartiesOnly()
    {
        var (alice, bob, open) = await OpenCovenantMatchAsync("lifecycle");
        var start = InMemoryChainService.FaucetSats;

        // The challenger stakes; the defender vanishes without staking.
        await alice.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        var staked = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(start - 4_000, staked.BalanceSats);

        // Before expiry: locked (the FORFEIT_CLOSURE_LOCKED analogue).
        var early = await alice.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("locked until", await early.Content.ReadAsStringAsync());

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // A non-party can never trigger anything.
        var (mallory, _) = await _factory.RegisterAsync("R-Mallory");
        var outsider = await mallory.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.BadRequest, outsider.StatusCode);
        Assert.Contains("Not a party", await outsider.Content.ReadAsStringAsync());

        // The defender never staked — nothing for THEM to refund.
        var unstaked = await bob.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.BadRequest, unstaked.StatusCode);
        Assert.Contains("Nothing staked", await unstaked.Content.ReadAsStringAsync());

        // After expiry the staker reclaims exactly their stake…
        var refund = await alice.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.OK, refund.StatusCode);
        var refunded = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(start, refunded.BalanceSats);

        // …and only once.
        var again = await alice.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("Nothing staked", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SettledEscrow_RefusesRefunds()
    {
        var (alice, bob, open) = await OpenCovenantMatchAsync("settled");
        await alice.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        var accept = (await (await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null))
            .Content.ReadFromJsonAsync<AcceptMatchResponse>())!;
        await bob.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        var fight = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight", new FightRequest("n"));
        Assert.Equal(HttpStatusCode.OK, fight.StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // past expiry — settlement must still win
        var refund = await alice.PostAsJsonAsync("/api/dev/refund-escrow", new { MatchId = open.MatchId });
        Assert.Equal(HttpStatusCode.BadRequest, refund.StatusCode);
        Assert.Contains("already settled", await refund.Content.ReadAsStringAsync());
    }
}
