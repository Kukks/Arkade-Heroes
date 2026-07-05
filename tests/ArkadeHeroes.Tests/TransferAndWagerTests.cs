using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Chain;
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
        var premature = await alice.PostAsJsonAsync($"/api/heroes/{gift.Id}/transfer",
            new TransferRequest(bobPlayer.PlayerId));
        Assert.Equal(HttpStatusCode.BadRequest, premature.StatusCode);

        // The (simulated) client wallet moves the asset, then confirm passes.
        await alice.TransferAssetAsync(gift.AssetId!, bobPlayer.PlayerId);
        var response = await alice.PostAsJsonAsync($"/api/heroes/{gift.Id}/transfer",
            new TransferRequest(bobPlayer.PlayerId));
        response.EnsureSuccessStatusCode();
        var transfer = (await response.Content.ReadFromJsonAsync<TransferResponse>())!;
        Assert.Equal(bobPlayer.PlayerId, transfer.Hero.OwnerId);

        // Bob now owns it; Alice doesn't.
        var bobHeroes = (await bob.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.Contains(bobHeroes, h => h.Id == gift.Id);
        var aliceMine = (await alice.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.DoesNotContain(aliceMine, h => h.Id == gift.Id);

        // Alice can no longer act with it.
        var stale = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(gift.Id, aliceHeroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
    }

    [Fact]
    public async Task WageredMatchInvoicesStakesAndPaysWinner()
    {
        var (alice, _) = await _factory.RegisterAsync("W-Alice");
        var (bob, _) = await _factory.RegisterAsync("W-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        const long wager = 5_000;
        var start = InMemoryChainService.FaucetSats;

        // Open: challenger receives a stake invoice.
        var openResponse = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager));
        openResponse.EnsureSuccessStatusCode();
        var open = (await openResponse.Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        Assert.NotNull(open.StakeInvoice);
        Assert.Equal(wager, open.StakeInvoice!.AmountSats);

        // Fight before stakes settle is rejected (defender hasn't even accepted).
        var early = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("early"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        await alice.PayInvoiceAsync(open.StakeInvoice.InvoiceId);
        var aliceAfterStake = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(start - wager, aliceAfterStake.BalanceSats);

        // Only the defender's owner may accept.
        var wrongAccept = await alice.PostAsync($"/api/matches/{open.MatchId}/accept", null);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAccept.StatusCode);

        // Accept: defender receives their invoice; fight blocked until paid.
        var acceptResponse = await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null);
        acceptResponse.EnsureSuccessStatusCode();
        var accept = (await acceptResponse.Content.ReadFromJsonAsync<AcceptMatchResponse>())!;

        var unpaidFight = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("unpaid"));
        Assert.Equal(HttpStatusCode.BadRequest, unpaidFight.StatusCode);

        await bob.PayInvoiceAsync(accept.StakeInvoice.InvoiceId);
        // Both fighters also pay their per-character match fee before the duel.
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        // Duel: pot pays the winner's owner; replay audit still holds.
        var fightResponse = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("duel-nonce"));
        fightResponse.EnsureSuccessStatusCode();
        var fight = (await fightResponse.Content.ReadFromJsonAsync<FightResponse>())!;
        Assert.Equal(wager, fight.WagerSats);
        Assert.Equal(wager * 2, fight.WinnerPayoutSats);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "duel-nonce", open.CommitmentHex, fight);
        Assert.True(ok, detail);

        var aliceFinal = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var bobFinal = (await bob.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
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
        var again = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("again"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task WagerAgainstOwnHeroIsRejected()
    {
        var (alice, _) = await _factory.RegisterAsync("W-Self");
        var heroes = await alice.ClaimStartersAsync();
        var response = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(heroes[0].Id, heroes[1].Id, 1_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
