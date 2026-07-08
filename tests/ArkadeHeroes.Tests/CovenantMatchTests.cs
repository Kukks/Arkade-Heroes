using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
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
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager, "covenant"));
        Assert.Null(open.StakeInvoice);
        Assert.NotNull(open.EscrowAddress);
        Assert.Equal(wager, open.EscrowStakeSats);

        // Fight is blocked until BOTH stakes sit in the escrow.
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        Assert.Null(accept.StakeInvoice);
        // Per-party escrows: each side stakes into their OWN address.
        Assert.NotNull(accept.EscrowAddress);
        Assert.NotEqual(open.EscrowAddress, accept.EscrowAddress);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.FightAsync(open.MatchId, new FightRequest("early")));

        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        // Both fighters also pay their per-character match fee before the duel.
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        // Duel: settlement comes from the ESCROW, not a treasury payout.
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("covenant-duel"));
        Assert.Equal(wager * 2, fight.WinnerPayoutSats);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "covenant-duel", open.CommitmentHex, fight);
        Assert.True(ok, detail);

        var aliceFinal = await alice.Players.MeAsync();
        var bobFinal = await bob.Players.MeAsync();
        var challengerWon = fight.Result.WinnerId == aliceHeroes[0].Id;
        var (winnerBalance, loserBalance) = challengerWon
            ? (aliceFinal.BalanceSats, bobFinal.BalanceSats)
            : (bobFinal.BalanceSats, aliceFinal.BalanceSats);
        // Each fighter also paid their level-proportional match fee to the treasury.
        var (winnerFee, loserFee) = challengerWon
            ? (open.MatchFeeInvoice!.AmountSats, accept.MatchFeeInvoice!.AmountSats)
            : (accept.MatchFeeInvoice!.AmountSats, open.MatchFeeInvoice!.AmountSats);
        Assert.Equal(start + wager - winnerFee, winnerBalance);
        Assert.Equal(start - wager - loserFee, loserBalance);
    }

    [Fact]
    public async Task CovenantModeRequiresAWager()
    {
        var (alice, _) = await _factory.RegisterAsync("C-NoWager");
        var heroes = await alice.ClaimStartersAsync();
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.OpenAsync(new OpenMatchRequest(heroes[0].Id, heroes[1].Id, 0, "covenant")));
    }
}
