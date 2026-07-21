using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// LOAD-BEARING PROSE. The browser's <c>GameSession</c> decides whether an action is retryable by grepping
/// the server's error text (e.g. <c>ex.Message.Contains("not been paid")</c>) — every covenant/fee flow
/// retries while a deposit settles into arkd's indexer. Nothing else couples those two files, and
/// <c>GameSession</c> has no tests of its own, so rewording a server message would silently turn a
/// retryable "still settling" into a hard failure in the live browser and nobody would notice.
///
/// These tests drive the REAL server error paths and assert the substrings GameSession greps for still
/// appear. If one fails, don't just fix the string here — update the matching predicate in
/// <c>src/ArkadeHeroes.Web/Wallet/GameSession.cs</c> too.
///
/// COVERED: "breed escrow", "merge escrow", "not been paid", "must stake", "hasn't been paid", "unpaid".
/// NOT COVERED (no cheap in-memory trigger): "not fully funded" (covenant-mode escrow underfund) and
/// "chain does not show" (hero transfer confirm / offer claim before the chain shows the asset). Those two
/// predicates remain unguarded — a deliberate, named gap rather than a silent one.
/// </summary>
public class BrowserRetryContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public BrowserRetryContractTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CovenantBreedReveal_SaysBreedEscrow()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-Breed-Cov");
        var heroes = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("n")));
        Assert.Contains("breed escrow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvoiceBreedReveal_SaysNotBeenPaid()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-Breed-Inv");
        var heroes = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("n")));
        Assert.Contains("not been paid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeReveal_SaysMergeEscrow()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-Merge");
        var heroes = await alice.ClaimStartersAsync();
        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("n")));
        Assert.Contains("merge escrow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GauntletRun_SaysNotBeenPaid()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-Gauntlet");
        var hero = (await alice.ClaimStartersAsync())[0];
        var open = await alice.Gauntlet.OpenAsync(hero.Id);

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Gauntlet.RunAsync(open.GauntletId, "n"));
        Assert.Contains("not been paid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeathMatchSettle_UnstakedSaysMustStake_ThenUnpaidFeeSaysHasntBeenPaid()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-DM-A");
        var (bob, _) = await _factory.RegisterAsync("Retry-DM-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);

        // Defender hasn't staked yet → the "still staking" retry predicate.
        var unstaked = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
        Assert.Contains("must stake", unstaked.Message, StringComparison.OrdinalIgnoreCase);

        // Both staked, but neither fee invoice is paid → the "fee still settling" retry predicate.
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        var unpaidFee = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
        Assert.Contains("hasn't been paid", unpaidFee.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StakedMatchFight_SaysUnpaid()
    {
        var (alice, _) = await _factory.RegisterAsync("Retry-Match-A");
        var (bob, _) = await _factory.RegisterAsync("Retry-Match-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero, bobHero, 1000, "invoice"));
        await bob.Matches.AcceptAsync(open.MatchId);

        // Nothing paid → the browser's "stakes still settling" retry predicate.
        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Matches.FightAsync(open.MatchId, new FightRequest("n")));
        Assert.Contains("unpaid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
