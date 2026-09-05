using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

public class TransferAndWagerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TransferAndWagerTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    [Fact]
    public async Task TransferIsClientSignedAndServerVerified()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("T-Alice");
        var (bob, bobPlayer) = await _factory.RegisterAsync("T-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        await bob.ClaimStartersAsync();
        var gift = aliceHeroes[0];

        // Confirming BEFORE the client wallet moved the asset must fail —
        // the server verifies the chain, it doesn't move anything itself.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Heroes.TransferAsync(gift.Id, new TransferRequest(bobPlayer.PlayerId)));

        // The (simulated) client wallet moves the asset, then confirm passes.
        await alice.TransferAssetAsync(gift.AssetId!, bobPlayer.PlayerId);
        var transfer = await alice.Heroes.TransferAsync(gift.Id, new TransferRequest(bobPlayer.PlayerId));
        Assert.Equal(bobPlayer.PlayerId, transfer.Hero.OwnerId);

        // Bob now owns it; Alice doesn't.
        var bobHeroes = await bob.Heroes.MineAsync();
        Assert.Contains(bobHeroes, h => h.Id == gift.Id);
        var aliceMine = await alice.Heroes.MineAsync();
        Assert.DoesNotContain(aliceMine, h => h.Id == gift.Id);

        // Alice can no longer act with it.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.CommitAsync(new BreedCommitRequest(gift.Id, aliceHeroes[1].Id)));
    }

    /// <summary>The staked duel, to the sat. Every number below survived the move off the treasury path
    /// unchanged — only WHERE the stake rests in between changed — so this is the evidence that going
    /// non-custodial cost the player nothing.</summary>
    [Fact]
    public async Task WageredMatchEscrowsStakesAndPaysWinner()
    {
        // Exact stake arithmetic below, so the starter claim must not move the balances first.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("W-Alice");
        var (bob, _) = await factory.RegisterAsync("W-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        const long wager = 5_000;
        var start = InMemoryChainService.FaucetSats;

        // Open: challenger receives their own escrow address, never an invoice to the treasury.
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager));
        Assert.Null(open.StakeInvoice);
        Assert.NotNull(open.EscrowAddress);
        Assert.Equal(wager, open.EscrowStakeSats);

        // Fight before stakes settle is rejected (defender hasn't even accepted).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.FightAsync(open.MatchId, new FightRequest("early")));

        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var aliceAfterStake = await alice.Players.MeAsync();
        Assert.Equal(start - wager, aliceAfterStake.BalanceSats);

        // Only the defender's owner may accept.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Matches.AcceptAsync(open.MatchId));

        // Accept: defender gets a SEPARATE escrow; fight blocked until they stake into it.
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        Assert.Null(accept.StakeInvoice);
        Assert.NotEqual(open.EscrowAddress, accept.EscrowAddress);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.FightAsync(open.MatchId, new FightRequest("unstaked")));

        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        Assert.Equal(start - wager, (await bob.Players.MeAsync()).BalanceSats);
        // Both fighters also pay their per-character match fee before the duel.
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        // Duel: pot pays the winner's owner; replay audit still holds.
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("duel-nonce"));
        Assert.Equal(wager, fight.WagerSats);
        Assert.Equal(wager * 2, fight.WinnerPayoutSats);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "duel-nonce", open.CommitmentHex, fight);
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

        // Settled matches can't be re-fought.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.FightAsync(open.MatchId, new FightRequest("again")));
    }

    [Fact]
    public async Task WagerAgainstOwnHeroIsRejected()
    {
        var (alice, _) = await _factory.RegisterAsync("W-Self");
        var heroes = await alice.ClaimStartersAsync();
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.OpenAsync(new OpenMatchRequest(heroes[0].Id, heroes[1].Id, 1_000)));
    }
}
