using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Covenant-mode wagered matches over the InMemory escrow simulation: stakes
/// go INTO the escrow (not invoices), the fight is gated on full funding, and
/// settlement pays the winner from the escrow — same lifecycle the NArk mode
/// enforces via the emulator on regtest.
/// </summary>
public class CovenantMatchTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CovenantMatchTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CovenantMatch_EscrowGatesTheFight_SettlementPaysTheWinner()
    {
        var (alice, _) = await _factory.RegisterAsync("C-Alice");
        var (bob, _) = await _factory.RegisterAsync("C-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        const long wager = 4_000;
        var start = InMemoryChainService.FaucetSats;

        // Open in covenant mode: an escrow address, no invoice.
        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager, "covenant")))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        Assert.Null(open.StakeInvoice);
        Assert.NotNull(open.EscrowAddress);
        Assert.Equal(wager, open.EscrowStakeSats);

        // Fight is blocked until BOTH stakes sit in the escrow.
        await alice.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        var accept = (await (await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null))
            .Content.ReadFromJsonAsync<AcceptMatchResponse>())!;
        Assert.Null(accept.StakeInvoice);
        // Per-party escrows: each side stakes into their OWN address.
        Assert.NotNull(accept.EscrowAddress);
        Assert.NotEqual(open.EscrowAddress, accept.EscrowAddress);

        var underfunded = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("early"));
        Assert.Equal(HttpStatusCode.BadRequest, underfunded.StatusCode);

        await bob.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });

        // Duel: settlement comes from the ESCROW, not a treasury payout.
        var fight = (await (await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
                new FightRequest("covenant-duel")))
            .Content.ReadFromJsonAsync<FightResponse>())!;
        Assert.Equal(wager * 2, fight.WinnerPayoutSats);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "covenant-duel", open.CommitmentHex, fight);
        Assert.True(ok, detail);

        var aliceFinal = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var bobFinal = (await bob.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var challengerWon = fight.Result.WinnerId == aliceHeroes[0].Id;
        var (winnerBalance, loserBalance) = challengerWon
            ? (aliceFinal.BalanceSats, bobFinal.BalanceSats)
            : (bobFinal.BalanceSats, aliceFinal.BalanceSats);
        Assert.Equal(start + wager, winnerBalance);
        Assert.Equal(start - wager, loserBalance);
    }

    [Fact]
    public async Task CovenantModeRequiresAWager()
    {
        var (alice, _) = await _factory.RegisterAsync("C-NoWager");
        var heroes = await alice.ClaimStartersAsync();
        var response = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(heroes[0].Id, heroes[1].Id, 0, "covenant"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
