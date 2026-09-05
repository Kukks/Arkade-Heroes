using ArkadeHeroes.Chain;
using Covenants = ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The daily faucet's crash-window durability: the day-consuming latch (<c>LastClaimDay</c>) must be
/// durable BEFORE the payout moves real sats — the same written-before-any-sat-moves rule the tournament
/// resolved marker and the starter reservation follow. If the process dies between the payout and a later
/// persist, a restart rehydrates the player as unclaimed and the faucet pays the same day twice out of a
/// treasury that can't print. The flip side must stay true in-process: a payout that fails CLEANLY
/// releases the day in memory only (never durably), so the player retries instead of forfeiting.
/// </summary>
public class DailyDurabilityGuardTests
{
    [Fact]
    public async Task CrashBetweenPayoutAndPersist_NeverPaysTheSameDayTwice()
    {
        // ONE chain instance spans both hosts — the chain is the outside world and survives a server
        // restart, so its settled-payout count is the ground truth for "how many times the treasury paid".
        var chain = new PayoutProbeChain(new InMemoryChainService());
        chain.Inner.FundTreasury(50_000);
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-daily-durability-{Guid.NewGuid():N}.db");
        try
        {
            string playerId;
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.UseSetting("Game:DailyRewardEnabled", "true");
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IChainService>(chain);
                    // The crash: the process "dies" the instant the daily payout settles. Writes issued
                    // before that instant are on disk; writes issued after are lost with the process.
                    s.AddSingleton<IGameStatePersistence>(sp => new CrashWindowPersistence(
                        new SqliteGameStatePersistence(sp.GetRequiredService<IDbContextFactory<GameStateDbContext>>()),
                        processDied: () => chain.DailyPayoutsPaid > 0));
                });
            }))
            {
                var (alice, dto) = await first.RegisterAsync("Daily-Crash");
                playerId = dto.PlayerId;
                await alice.ClaimStartersAsync();
                var claim = await alice.Daily.ClaimAsync();
                Assert.True(claim.AwardedSats > 0);   // a real payout settled — the crash window is live
                Assert.Equal(1, chain.DailyPayoutsPaid);
            }

            // ── restart: a fresh host and GameStore rehydrate from whatever survived the crash ──
            using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                // The faucet must be OPEN on the restarted host too. Closed, the claim below still throws
                // GameRuleException — but for "not available on this server", which would let this test
                // pass without ever exercising the durable already-claimed guard it exists to prove.
                b.UseSetting("Game:DailyRewardEnabled", "true");
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            });
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();
            var svc = restarted.Services.GetRequiredService<GameService>();
            var player = store.Players[playerId];

            // The day was consumed durably before the sats moved, so the rehydrated player is refused —
            // a re-issued `daily:{day}` payout is a second real payment the treasury can never reclaim.
            await Assert.ThrowsAsync<GameRuleException>(() => svc.ClaimDailyAsync(player, CancellationToken.None));
            Assert.Equal(1, chain.DailyPayoutsPaid);
        }
        finally
        {
            // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
            // file is harmless either way — never fail a durability test on its own housekeeping.
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CleanPayoutFailure_LeavesTheDayClaimable_AndTheRetryPaysOnce()
    {
        // The other half of the invariant pair: durably consuming the day BEFORE paying must not turn a
        // clean payout failure into a forfeited day. The release is in-memory only — never re-persisted,
        // because if the payout actually settled before throwing, the durable consume is the one thing
        // standing between a restart and a second payment.
        var chain = new PayoutProbeChain(new InMemoryChainService());
        chain.Inner.FundTreasury(50_000);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:DailyRewardEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
        });

        var (alice, dto) = await factory.RegisterAsync("Daily-Retry");
        await alice.ClaimStartersAsync();
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[dto.PlayerId];

        chain.FailNextDailyPayout = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClaimDailyAsync(player, CancellationToken.None));

        Assert.Null(player.LastClaimDay);          // the failed payout did NOT consume the day
        Assert.Equal(0, player.StreakCount);       // the streak rolled back with it
        Assert.Equal(0, chain.DailyPayoutsPaid);   // and no sats moved

        var claim = await svc.ClaimDailyAsync(player, CancellationToken.None);
        Assert.True(claim.AwardedSats > 0);
        Assert.NotNull(player.LastClaimDay);       // the retry consumed the day…
        Assert.Equal(1, chain.DailyPayoutsPaid);   // …and paid exactly once
    }

    /// <summary>Delegates to the real SQLite persistence until <paramref name="processDied"/> flips, then
    /// silently drops every write — the deterministic stand-in for a process crash at that instant: nothing
    /// issued after the moment of death ever reaches disk. Loads always delegate (the restarted process reads
    /// whatever the dead one managed to write).</summary>
    private sealed class CrashWindowPersistence(IGameStatePersistence inner, Func<bool> processDied) : IGameStatePersistence
    {
        public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => inner.LoadIntoAsync(store, ct);
        public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveItemPurchaseAsync(purchase, ct);
        public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveTournamentAsync(session, ct);
        public Task SavePlayerAsync(Player player, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SavePlayerAsync(player, ct);
        public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveFancyFindAsync(find, ct);
        public Task SaveHeroAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroAsync(hero, ct);
        public Task SaveHeroProgressionAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroProgressionAsync(hero, ct);
        public Task DeleteHeroAsync(string heroId, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.DeleteHeroAsync(heroId, ct);
        public Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveOfferAsync(offer, ct);
        public Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveStudProposalAsync(proposal, ct);
        public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroSaleAsync(sale, ct);
        public Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroTombstoneAsync(stone, ct);
        public Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroBidAsync(bid, ct);
        public Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveTreasuryFlowAsync(id, direction, tag, sats, ct);
    }

    /// <summary>Delegates to the real InMemory sim but counts SETTLED daily-faucet payouts (memo tag
    /// `daily:{day}`) and can fault the next one — the deterministic stand-ins for "the sats actually left
    /// the treasury" and "the payout failed cleanly before any sat moved".</summary>
    private sealed class PayoutProbeChain(InMemoryChainService inner) : IChainService, ISimulatedChain
    {
        public InMemoryChainService Simulator => inner;
        private int _dailyPayoutsPaid;
        public InMemoryChainService Inner => inner;
        public int DailyPayoutsPaid => Volatile.Read(ref _dailyPayoutsPaid);
        public volatile bool FailNextDailyPayout;

        public async Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default)
        {
            if (!memo.StartsWith("daily:", StringComparison.Ordinal))
                return await inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
            if (FailNextDailyPayout)
            {
                FailNextDailyPayout = false;
                throw new InvalidOperationException("Simulated payout fault (injected by test).");
            }
            var txId = await inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
            Interlocked.Increment(ref _dailyPayoutsPaid);
            return txId;
        }

        public Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default) => inner.MintHeroAssetAsync(toPlayerId, data, ct);
        public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);
        public Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default) => inner.RegisterPlayerAddressAsync(playerId, arkadeAddress, ct);
        public Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default) => inner.GetPlayerAddressAsync(playerId, ct);
        public Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default) => inner.GetAddressBalanceSatsAsync(playerId, ct);
        public Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default) => inner.CreateFeeInvoiceAsync(memo, amountSats, ct);
        public Task<FeeInvoice?> GetFeeInvoiceAsync(string invoiceId, CancellationToken ct = default) => inner.GetFeeInvoiceAsync(invoiceId, ct);
        public Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default) => inner.IsInvoicePaidAsync(invoiceId, ct);
        public Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default) => inner.DeliverItemAssetAsync(toPlayerId, itemId, itemName, ct);
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
        public Task<OfferInfo> CreateOfferAsync(string offerId, string sellerPlayerId, string itemId, long askSats, long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default) => inner.CreateOfferAsync(offerId, sellerPlayerId, itemId, askSats, refundAfterUnixSeconds, feeSats, ct);
        public Task<OfferInfo> CreateHeroOfferAsync(string offerId, string sellerPlayerId, string heroAssetId, long askSats, long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default) => inner.CreateHeroOfferAsync(offerId, sellerPlayerId, heroAssetId, askSats, refundAfterUnixSeconds, feeSats, ct);
        public Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default) => inner.IsOfferFundedAsync(offerId, ct);
        public Task<bool> WasOfferSoldAsync(string offerId, CancellationToken ct = default) => inner.WasOfferSoldAsync(offerId, ct);
        public Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default) => inner.GetOfferParamsAsync(offerId, ct);
        public Task<string> SettleWagerEscrowAsync(string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleWagerEscrowAsync(matchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default) => inner.VerifyHeroOwnershipAsync(playerId, assetId, ct);
        public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default) => inner.GetItemAssetBalanceAsync(playerId, itemId, ct);
    }
}
