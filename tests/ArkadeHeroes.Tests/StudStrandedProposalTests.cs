using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using FailableChain = ArkadeHeroes.Tests.MoneyPathRaceGuardTests.FailableChain;

namespace ArkadeHeroes.Tests;

/// <summary>The way OUT of a stud proposal that can never be revealed: accepting bills BOTH fees, the reveal
/// books them before checks a sold-on or burned stud can never satisfy, and decline refuses once accepted.</summary>
public class StudStrandedProposalTests
{
    const long StudFee = 1_500;

    private sealed record Strand(
        string ProposalId, ArkadeHeroesClient Alice, ArkadeHeroesClient Bob, string StudHeroId,
        string BobPlayerId, string CarolPlayerId, long AliceBeforePaying, long BreedFee);

    private static async Task<Strand> AcceptedAndPaidAsync(
        WebApplicationFactory<Program> factory, string tag, bool payStudFee = true)
    {
        var (alice, _) = await factory.RegisterAsync($"{tag}-A");
        var (bob, bobDto) = await factory.RegisterAsync($"{tag}-B");
        var (carol, carolDto) = await factory.RegisterAsync($"{tag}-C");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var bobHeroes = await bob.ClaimStartersAsync();
        await carol.ClaimStartersAsync();

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, bobHeroes[0].Id, StudFee));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);
        var aliceBefore = (await alice.Players.MeAsync()).BalanceSats;
        await alice.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        if (payStudFee) await alice.PayInvoiceAsync(bill.StudFeeInvoice!.InvoiceId);

        return new Strand(proposal.ProposalId, alice, bob, bobHeroes[0].Id,
            bobDto.PlayerId, carolDto.PlayerId, aliceBefore, bill.BreedFeeInvoice.AmountSats);
    }

    private static async Task SellStudAsync(Strand s)
    {
        var asset = (await s.Bob.Heroes.GetAsync(s.StudHeroId)).AssetId!;
        await s.Bob.TransferAssetAsync(asset, s.CarolPlayerId);
        await s.Bob.Heroes.TransferAsync(s.StudHeroId, new TransferRequest(s.CarolPlayerId));
    }

    [Fact]
    public async Task AStudSoldAfterConsent_SendsBothOfTheProposersFeesHome()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Sold");
        var escrowed = s.BreedFee + StudFee;
        Assert.Equal(s.AliceBeforePaying - escrowed, (await s.Alice.Players.MeAsync()).BalanceSats);

        await SellStudAsync(s);

        // The refusal itself is CORRECT and stays — this adds an exit, it does not open the breed.
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => s.Alice.Stud.RevealAsync(s.ProposalId, new StudRevealRequest("sold")));
        Assert.Contains("changed hands", refused.Message);

        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var refund = await s.Alice.Stud.RefundAsync(s.ProposalId);

        Assert.Equal("refunded", refund.Proposal.Status);
        Assert.Equal(escrowed, refund.RefundedSats);
        Assert.Equal(treasuryBefore - escrowed, await chain.TreasuryBalanceAsync());
        Assert.Equal(s.AliceBeforePaying, (await s.Alice.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task AStudBurnedAfterConsent_SendsBothOfTheProposersFeesHome()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Burned");
        var sacrifice = (await s.Bob.Heroes.MineAsync()).First(h => h.Id != s.StudHeroId).Id;
        var merge = await s.Bob.Merge.CommitAsync(new MergeCommitRequest(s.StudHeroId, sacrifice));
        await s.Bob.Dev.FundMergeEscrowAsync(new { merge.MergeId });
        await s.Bob.Merge.RevealAsync(merge.MergeId, new MergeRevealRequest("burn-the-stud"));

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => s.Alice.Stud.RevealAsync(s.ProposalId, new StudRevealRequest("burned")));
        Assert.Contains("Unknown hero", refused.Message);

        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var refund = await s.Alice.Stud.RefundAsync(s.ProposalId);

        Assert.Equal(s.BreedFee + StudFee, refund.RefundedSats);
        Assert.Equal(treasuryBefore - (s.BreedFee + StudFee), await chain.TreasuryBalanceAsync());
        Assert.Equal(s.AliceBeforePaying, (await s.Alice.Players.MeAsync()).BalanceSats);
    }

    /// <summary>The line this must not cross: a cooldown is a WAIT, not a death.</summary>
    [Fact]
    public async Task AStudMerelyOnBreedingCooldown_IsNotRefundable()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Cooldown");
        var mate = (await s.Bob.Heroes.MineAsync()).First(h => h.Id != s.StudHeroId).Id;
        await s.Bob.BreedAsync(s.StudHeroId, mate, "put-the-stud-on-cooldown");

        var cannotReveal = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => s.Alice.Stud.RevealAsync(s.ProposalId, new StudRevealRequest("too-soon")));
        Assert.Contains("cooldown", cannotReveal.Message);

        var escrowed = await chain.TreasuryBalanceAsync();
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => s.Alice.Stud.RefundAsync(s.ProposalId));
        Assert.Contains("can still be revealed", refused.Message);
        Assert.Equal(escrowed, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task ARefund_ReturnsOnlyTheFeesThatActuallyCleared()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Partial", payStudFee: false);
        await SellStudAsync(s);

        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var refund = await s.Alice.Stud.RefundAsync(s.ProposalId);

        Assert.Equal(s.BreedFee, refund.RefundedSats);
        Assert.Equal(treasuryBefore - s.BreedFee, await chain.TreasuryBalanceAsync());
        Assert.Equal(s.AliceBeforePaying, (await s.Alice.Players.MeAsync()).BalanceSats);
    }

    /// <summary>THE dangerous case: a faulting mint leaves an unfinished proposal whose stud fee is gone.</summary>
    [Fact]
    public async Task AStudFeeAlreadyForwarded_IsNeverPaidBackToTheProposer()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        chain.Inner.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Forwarded");
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var alicePlayer = store.StudProposals[s.ProposalId].ProposerPlayerId;
        var bobBefore = (await s.Bob.Players.MeAsync()).BalanceSats;

        chain.FailNextHeroMint = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevealStudAsync(store.Players[alicePlayer], s.ProposalId, "mint-faults", CancellationToken.None));

        Assert.Equal(bobBefore + StudFee, (await s.Bob.Players.MeAsync()).BalanceSats);
        Assert.True(store.StudProposals[s.ProposalId].StudFeePaid);
        Assert.False(store.StudProposals[s.ProposalId].Completed);

        await SellStudAsync(s);
        var treasuryBefore = await chain.Inner.TreasuryBalanceAsync();
        var refund = await s.Alice.Stud.RefundAsync(s.ProposalId);

        Assert.Equal(s.BreedFee, refund.RefundedSats);
        Assert.Equal(treasuryBefore - s.BreedFee, await chain.Inner.TreasuryBalanceAsync());
        Assert.Equal(bobBefore + StudFee, (await s.Bob.Players.MeAsync()).BalanceSats);
        Assert.Equal(s.AliceBeforePaying - StudFee, (await s.Alice.Players.MeAsync()).BalanceSats);
    }

    /// <summary>Single-shot both ways, or one consent spends fees that have already gone home.</summary>
    [Fact]
    public async Task ARefundedProposal_CanNeitherBeRefundedAgainNorRevealed()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Once");
        await SellStudAsync(s);
        await s.Alice.Stud.RefundAsync(s.ProposalId);

        var settled = await chain.TreasuryBalanceAsync();
        var again = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => s.Alice.Stud.RefundAsync(s.ProposalId));
        Assert.Contains("already refunded", again.Message);

        var revealed = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => s.Alice.Stud.RevealAsync(s.ProposalId, new StudRevealRequest("after-refund")));
        Assert.Contains("refunded", revealed.Message);
        Assert.Equal(settled, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task AStrandedProposal_IsSurfacedOnTheProposersReclaimList()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var s = await AcceptedAndPaidAsync(factory, "Stud-Strand-Reclaim");
        Assert.DoesNotContain(await s.Alice.Players.ReclaimableAsync(), r => r.Kind == "stud");

        await SellStudAsync(s);

        var row = Assert.Single(await s.Alice.Players.ReclaimableAsync(), r => r.Kind == "stud");
        Assert.Equal(s.ProposalId, row.Id);
        Assert.Equal(0, row.ReclaimAfterUnixSeconds);

        await s.Alice.Stud.RefundAsync(s.ProposalId);
        Assert.DoesNotContain(await s.Alice.Players.ReclaimableAsync(), r => r.Kind == "stud");
    }
}
