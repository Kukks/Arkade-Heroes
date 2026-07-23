using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using Covenants = ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Money-path double-execute races: every once-only guard (daily claimed, starters claimed, match
/// resolved, session completed) must be checked and latched atomically around its NON-IDEMPOTENT
/// chain effect (payout / mint / delivery). The client poll-retries, so concurrent same-key requests
/// are the normal case, not an edge: N tasks released on one barrier must land the effect EXACTLY
/// once. Fresh factory per test — the treasury and store are global singletons.
/// </summary>
public class MoneyPathRaceGuardTests
{
    private const int Racers = 64;

    /// <summary>Releases <paramref name="racers"/> calls of <paramref name="action"/> on one barrier and
    /// counts how many completed without throwing (the losers must hit the flow's already-done rule).</summary>
    private static async Task<int> RaceAsync(int racers, Func<Task> action)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try { await action(); return true; }
            catch { return false; }
        })).ToList();
        start.SetResult();
        return (await Task.WhenAll(tasks)).Count(won => won);
    }

    [Fact]
    public async Task ConcurrentDailyClaims_PayTheFaucetExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, dto) = await factory.RegisterAsync("Race-Daily");
        await alice.ClaimStartersAsync();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(10_000);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[dto.PlayerId];
        var expected = (await alice.Daily.StatusAsync()).ClaimableNowSats;   // one base reward, no quests
        Assert.True(expected > 0);
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        var wins = await RaceAsync(Racers, () => svc.ClaimDailyAsync(player, CancellationToken.None));

        Assert.Equal(1, wins);                                                         // one claim consumed the day
        Assert.Equal(expected, store.TreasuryOutflowByTag.GetValueOrDefault("daily")); // paid once, not per racer
        Assert.Equal(treasuryBefore - expected, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task ConcurrentStarterClaims_MintExactlyTwoHeroes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (_, dto) = await factory.RegisterAsync("Race-Starters");
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[dto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.ClaimStartersAsync(player, CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(2, store.Heroes.Values.Count(h => h.OwnerId == player.Id));   // the pair, never four
        Assert.True(player.StarterClaimed);
    }

    [Fact]
    public async Task ConcurrentFightResolves_PayThePotAndMoveXpExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Fight-A");
        var (bob, _) = await factory.RegisterAsync("Race-Fight-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);   // covers even a double pot, so an over-payout shows instead of faulting

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id, 1000, "invoice"));
        await alice.PayInvoiceAsync(open.StakeInvoice!.InvoiceId);
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.PayInvoiceAsync(accept.StakeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.FightAsync(player, open.MatchId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("wager"));            // the 2×stake pot, once
        Assert.Equal(1, store.ReceiptsByHero[aliceHero.Id].Count(r => r.Id == open.MatchId)); // XP transferred once
    }

    [Fact]
    public async Task ConcurrentSquadResolves_PayThePotExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Squad-A");
        var (bob, _) = await factory.RegisterAsync("Race-Squad-B");
        var mine = (await alice.ClaimStartersAsync()).Select(h => h.Id).ToList();
        mine.Add((await alice.Dev.MintHeroAsync()).Id);
        var theirs = (await bob.ClaimStartersAsync()).Select(h => h.Id).ToList();
        theirs.Add((await bob.Dev.MintHeroAsync()).Id);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1000, "invoice"));
        await alice.PayInvoiceAsync(open.StakeInvoice!.InvoiceId);
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Squad.AcceptAsync(open.MatchId);
        await bob.PayInvoiceAsync(accept.StakeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.ResolveSquadMatchAsync(player, open.MatchId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("squad"));   // the 2×stake pot, once
        Assert.Equal(1, store.ReceiptsByHero[mine[0]].Count(r => r.Id.StartsWith($"{open.MatchId}:")));   // slot-0 duel scored once
    }

    [Fact]
    public async Task ConcurrentBreedReveals_MintExactlyOneChild()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Breed");
        var parents = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(parents[0].Id, parents[1].Id));
        await alice.PayInvoiceAsync(commit.Invoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.RevealBreedingAsync(player, commit.BreedingId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(3, store.Heroes.Values.Count(h => h.OwnerId == player.Id));   // 2 parents + ONE child
        Assert.Equal(1, store.Heroes[parents[0].Id].BreedCount);                   // breed bookkeeping applied once
    }

    [Fact]
    public async Task BreedReveal_MintFailure_LeavesSessionOpenForRetry()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Breed-Retry");
        var parents = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(parents[0].Id, parents[1].Id));
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, commit.Invoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        // The child mint faults AFTER the fee was verified paid: the session must NOT latch Completed
        // (or burn the parents' cooldowns), else the paid fee is stranded with no retry.
        chain.FailNextHeroMint = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevealBreedingAsync(player, commit.BreedingId, "retry-nonce", CancellationToken.None));

        Assert.False(store.Breedings[commit.BreedingId].Completed);   // still open — the fee is retryable
        Assert.Equal(0, store.Heroes[parents[0].Id].BreedCount);      // no cooldown burned on a failed mint

        // And the retry succeeds: the SAME paid session mints the child.
        var (child, _, _, _) = await svc.RevealBreedingAsync(player, commit.BreedingId, "retry-nonce", CancellationToken.None);
        Assert.Equal(player.Id, child.OwnerId);
        Assert.True(store.Breedings[commit.BreedingId].Completed);
        Assert.Equal(1, store.Heroes[parents[0].Id].BreedCount);
    }

    [Fact]
    public async Task ConcurrentGauntletRuns_ResolveExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Gauntlet");
        var hero = (await alice.ClaimStartersAsync())[0];
        var open = await alice.Gauntlet.OpenAsync(hero.Id);
        await alice.PayInvoiceAsync(open.FeeInvoice.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.RunGauntletAsync(player, open.GauntletId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(1, store.ReceiptsByHero[hero.Id].Count(r => r.Id == open.GauntletId));   // XP awarded once
        var delivered = 0UL;   // a full clear delivers ONE item unit; a partial clear none — never two
        foreach (var item in ArkadeHeroes.Core.Equipment.ItemCatalog.All)
            delivered += await chain.GetItemAssetBalanceAsync(player.Id, item.Id, CancellationToken.None);
        Assert.True(delivered <= 1);
    }

    /// <summary>Delegates to the real InMemory sim but can fault the NEXT hero mint — the deterministic
    /// stand-in for "the chain call failed after the fee was verified paid".</summary>
    private sealed class FailableChain(InMemoryChainService inner) : IChainService
    {
        public InMemoryChainService Inner => inner;
        public volatile bool FailNextHeroMint;

        public Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default)
        {
            if (FailNextHeroMint)
            {
                FailNextHeroMint = false;
                throw new InvalidOperationException("Simulated mint fault (injected by test).");
            }
            return inner.MintHeroAssetAsync(toPlayerId, data, ct);
        }

        public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);
        public Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default) => inner.RegisterPlayerAddressAsync(playerId, arkadeAddress, ct);
        public Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default) => inner.GetPlayerAddressAsync(playerId, ct);
        public Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default) => inner.GetAddressBalanceSatsAsync(playerId, ct);
        public Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default) => inner.CreateFeeInvoiceAsync(memo, amountSats, ct);
        public Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default) => inner.IsInvoicePaidAsync(invoiceId, ct);
        public Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default) => inner.DeliverItemAssetAsync(toPlayerId, itemId, itemName, ct);
        public Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default) => inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
        public Task<long> TreasuryBalanceAsync(CancellationToken ct = default) => inner.TreasuryBalanceAsync(ct);
        public Task<WagerEscrowInfo> CreateWagerEscrowAsync(string matchId, string challengerPlayerId, string defenderPlayerId, long stakeSats, byte[] seedCommitment32, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateWagerEscrowAsync(matchId, challengerPlayerId, defenderPlayerId, stakeSats, seedCommitment32, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsEscrowFundedAsync(string matchId, CancellationToken ct = default) => inner.IsEscrowFundedAsync(matchId, ct);
        public Task<WagerEscrowFunding?> GetWagerEscrowFundingAsync(string matchId, CancellationToken ct = default) => inner.GetWagerEscrowFundingAsync(matchId, ct);
        public Task<Covenants.WagerEscrowParams?> GetWagerEscrowParamsAsync(string matchId, CancellationToken ct = default) => inner.GetWagerEscrowParamsAsync(matchId, ct);
        public Task<BreedEscrowInfo> CreateBreedEscrowAsync(string breedingId, string playerId, string parentAAssetId, string parentBAssetId, long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateBreedEscrowAsync(breedingId, playerId, parentAAssetId, parentBAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsBreedEscrowFundedAsync(string breedingId, CancellationToken ct = default) => inner.IsBreedEscrowFundedAsync(breedingId, ct);
        public Task<HeroMintResult> ExecuteBreedCovenantAsync(string breedingId, HeroMintData childData, byte[] oracleSignature64, CancellationToken ct = default) => inner.ExecuteBreedCovenantAsync(breedingId, childData, oracleSignature64, ct);
        public Task<Covenants.BreedEscrowParams?> GetBreedEscrowParamsAsync(string breedingId, CancellationToken ct = default) => inner.GetBreedEscrowParamsAsync(breedingId, ct);
        public Task<string> CreateMergeEscrowAsync(string mergeId, string playerId, string baseAssetId, string sacrificeAssetId, long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateMergeEscrowAsync(mergeId, playerId, baseAssetId, sacrificeAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsMergeEscrowFundedAsync(string mergeId, CancellationToken ct = default) => inner.IsMergeEscrowFundedAsync(mergeId, ct);
        public Task<HeroMintResult> ExecuteMergeAsync(string mergeId, HeroMintData fusedData, byte[] oracleSignature64, CancellationToken ct = default) => inner.ExecuteMergeAsync(mergeId, fusedData, oracleSignature64, ct);
        public Task<Covenants.MergeEscrowParams?> GetMergeEscrowParamsAsync(string mergeId, CancellationToken ct = default) => inner.GetMergeEscrowParamsAsync(mergeId, ct);
        public Task<string> CreateDeathMatchJointEscrowAsync(string deathMatchId, string challengerPlayerId, string challengerHeroAssetId, string defenderPlayerId, string defenderHeroAssetId, byte[] seedCommitment32, string oraclePubKeyHex, long refundAfterUnixSeconds, IReadOnlyList<string>? challengerGearItemIds = null, IReadOnlyList<string>? defenderGearItemIds = null, bool absorb = false, string speciesId = "", CancellationToken ct = default) => inner.CreateDeathMatchJointEscrowAsync(deathMatchId, challengerPlayerId, challengerHeroAssetId, defenderPlayerId, defenderHeroAssetId, seedCommitment32, oraclePubKeyHex, refundAfterUnixSeconds, challengerGearItemIds, defenderGearItemIds, absorb, speciesId, ct);
        public Task<bool> IsDeathMatchEscrowFundedAsync(string deathMatchId, CancellationToken ct = default) => inner.IsDeathMatchEscrowFundedAsync(deathMatchId, ct);
        public Task<string> SettleDeathMatchAsync(string deathMatchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleDeathMatchAsync(deathMatchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<HeroMintResult> SettleDeathMatchAbsorbMintAsync(string deathMatchId, bool challengerWon, HeroMintData absorbedData, byte[] serverSeed, byte[] outcomeSignature64, byte[] rootSignature64, CancellationToken ct = default) => inner.SettleDeathMatchAbsorbMintAsync(deathMatchId, challengerWon, absorbedData, serverSeed, outcomeSignature64, rootSignature64, ct);
        public Task<Covenants.DeathMatchJointEscrowParams?> GetDeathMatchEscrowParamsAsync(string deathMatchId, CancellationToken ct = default) => inner.GetDeathMatchEscrowParamsAsync(deathMatchId, ct);
        public Task<OfferInfo> CreateOfferAsync(string offerId, string sellerPlayerId, string itemId, long askSats, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateOfferAsync(offerId, sellerPlayerId, itemId, askSats, refundAfterUnixSeconds, ct);
        public Task<OfferInfo> CreateHeroOfferAsync(string offerId, string sellerPlayerId, string heroAssetId, long askSats, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateHeroOfferAsync(offerId, sellerPlayerId, heroAssetId, askSats, refundAfterUnixSeconds, ct);
        public Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default) => inner.IsOfferFundedAsync(offerId, ct);
        public Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default) => inner.GetOfferParamsAsync(offerId, ct);
        public Task<string> SettleWagerEscrowAsync(string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleWagerEscrowAsync(matchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default) => inner.VerifyHeroOwnershipAsync(playerId, assetId, ct);
        public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default) => inner.GetItemAssetBalanceAsync(playerId, itemId, ct);
    }
}
