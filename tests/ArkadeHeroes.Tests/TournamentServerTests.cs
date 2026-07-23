using ArkadeHeroes.Chain;
using Covenants = ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The server tournament flow: players pay a buy-in into the treasury, fill a bracket, and once full it runs
/// the pure resolver and pays the podium (champion + runner-up, 70/30) out of the pot minus the house rake.
/// Treasury-mediated — the net treasury gain from a resolved tournament is exactly the rake.
/// </summary>
public class TournamentServerTests
{
    const long BuyIn = 1_000;

    static async Task<List<(ArkadeHeroesClient Client, string HeroId)>> FourPlayersAsync(WebApplicationFactory<Program> factory)
    {
        var players = new List<(ArkadeHeroesClient, string)>();
        for (var i = 0; i < 4; i++)
        {
            var (c, _) = await factory.RegisterAsync($"T-P{i}");
            var heroes = await c.ClaimStartersAsync();
            players.Add((c, heroes[0].Id));
        }
        return players;
    }

    [Fact]
    public async Task Tournament_FullFlow_PaysPodium_AndTreasuryNetsTheRake()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var players = await FourPlayersAsync(factory);
        var treasuryStart = await chain.TreasuryBalanceAsync();

        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });
        }

        var resolved = await players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("nonce-1"));
        Assert.Equal("resolved", resolved.Tournament.Status);
        Assert.NotNull(resolved.Tournament.ChampionHeroId);
        Assert.Equal(3, resolved.Bracket.Count);                             // 2 semis + 1 final
        Assert.Equal(new long[] { 2520, 1080 }, resolved.Prizes.ToArray());  // 3600 pool → 70/30
        Assert.Equal(2520, resolved.Tournament.ChampionPrizeSats);           // champion's share, surfaced for the Hall of Champions

        // Buy-ins (4000) in, prizes (3600) out → the treasury nets exactly the 10% rake (400).
        Assert.Equal(treasuryStart + BuyIn * 4 * 10 / 100, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task Tournament_ResolvedBracket_IsClientVerifiable()
    {
        // The bracket pays out real sats — so, like every other resolvable outcome, a client must be able to
        // re-run it from the revealed seed and trust nothing. Drive the real flow, pull the replay, verify.
        using var factory = new WebApplicationFactory<Program>();
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });
        }
        await players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("verify-nonce"));

        var replay = await players[0].Client.Tournament.ReplayAsync(tid);
        var verdict = FairnessAudit.VerifyTournament(tid, replay.Nonce, replay.CommitmentHex, replay);
        Assert.True(verdict.Ok, verdict.Detail);                 // the real bracket re-runs identically client-side
        Assert.Equal(4, replay.Entrants.Count);

        // …and a server that misreported the champion would be caught.
        Assert.False(FairnessAudit.VerifyTournament(tid, replay.Nonce, replay.CommitmentHex,
            replay with { ChampionHeroId = "phantom" }).Ok);
    }

    [Fact]
    public async Task Tournament_CannotResolveBeforeFull()
    {
        using var factory = new WebApplicationFactory<Program>();
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        await players[1].Client.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(players[1].HeroId));

        // Only 2 of 4 seats filled — not resolvable.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => players[0].Client.Tournament.ResolveAsync(open.Tournament.Id, new FightRequest("n")));
    }

    [Fact]
    public async Task Tournament_CannotResolveWithAnUnpaidBuyIn()
    {
        using var factory = new WebApplicationFactory<Program>();
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            if (i < 3) await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });   // leave the last unpaid
        }

        // Full bracket, but one buy-in is unpaid → refuse (an unpaid entry would leak the treasury).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("n")));
    }

    [Fact]
    public async Task Config_PublishesRakePct_ForDisplay()
    {
        // The tournaments page previews the house rake from GET /api/chain/info.
        using var factory = new WebApplicationFactory<Program>();
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var info = await client.Chain.InfoAsync();
        Assert.Equal(10, info.Config?.TournamentRakePct);   // GameOptions.TournamentRakePct default
    }

    // ── The strand-refund safety valve: a bracket whose entrant hero is GONE (heroes aren't persisted, so a
    // durable-mode restart loses them; a burn/merge loses one live) can never resolve — its paid buy-ins
    // would sit in the treasury forever. The refund returns them, exactly once. ──

    /// <summary>Fills a paid 4-player bracket and returns (players, tournamentId) — the shared refund setup.</summary>
    static async Task<(List<(ArkadeHeroesClient Client, string HeroId)> Players, string Tid)> PaidBracketAsync(
        WebApplicationFactory<Program> factory, int unpaidSeats = 0)
    {
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(players[i].HeroId));
            if (i < 4 - unpaidSeats) await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });
        }
        return (players, open.Tournament.Id);
    }

    [Fact]
    public async Task Refund_UnresolvableTournament_ReturnsEveryPaidBuyIn_Once()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var treasuryStart = await chain.TreasuryBalanceAsync();
        var (players, tid) = await PaidBracketAsync(factory);
        Assert.Equal(treasuryStart + BuyIn * 4, await chain.TreasuryBalanceAsync());   // 4 paid buy-ins, treasury-held

        // The strand: one entrant hero vanishes from the store — this bracket can never resolve again.
        store.Heroes.TryRemove(players[1].HeroId, out _);

        var refund = await players[0].Client.Tournament.RefundAsync(tid);
        Assert.Equal("refunded", refund.Tournament.Status);
        Assert.Equal(4, refund.EntrantsRefunded);
        Assert.Equal(BuyIn * 4, refund.RefundedSats);
        // Net-zero: 4 buy-ins in, 4 refunds out — the pot never paid, and the house takes no rake on a refund.
        Assert.Equal(treasuryStart, await chain.TreasuryBalanceAsync());
        Assert.Equal(BuyIn * 4, store.TreasuryOutflowByTag["tournament-refund"]);

        // Once only: the terminal `refunded` status refuses a second pass, and not a sat moves again.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => players[0].Client.Tournament.RefundAsync(tid));
        Assert.Equal(treasuryStart, await chain.TreasuryBalanceAsync());
        Assert.Equal(BuyIn * 4, store.TreasuryOutflowByTag["tournament-refund"]);
    }

    [Fact]
    public async Task Refund_ResolvableTournament_IsRefused()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (players, tid) = await PaidBracketAsync(factory);

        // Every entrant hero is present → the bracket can still run, so the refund must refuse to unwind
        // the pot — otherwise anyone facing a likely loss could cash the whole bracket out from under it.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => players[0].Client.Tournament.RefundAsync(tid));

        // …and the refusal changed nothing: the bracket still resolves normally.
        var resolved = await players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("still-live"));
        Assert.Equal("resolved", resolved.Tournament.Status);
    }

    [Fact]
    public async Task Refund_OnlyRefundsPaidBuyIns()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var treasuryStart = await chain.TreasuryBalanceAsync();
        var (players, tid) = await PaidBracketAsync(factory, unpaidSeats: 1);   // full bracket, last seat unpaid
        store.Heroes.TryRemove(players[1].HeroId, out _);                      // strand it

        // Only the three CLEARED buy-ins come back — the unpaid seat put nothing into the treasury, and
        // "refunding" it would pay out sats the treasury never received.
        var refund = await players[0].Client.Tournament.RefundAsync(tid);
        Assert.Equal("refunded", refund.Tournament.Status);
        Assert.Equal(3, refund.EntrantsRefunded);
        Assert.Equal(BuyIn * 3, refund.RefundedSats);
        Assert.Equal(treasuryStart, await chain.TreasuryBalanceAsync());       // 3 in, 3 out — net zero
        Assert.Equal(BuyIn * 3, store.TreasuryOutflowByTag["tournament-refund"]);
    }

    [Fact]
    public async Task Refund_PersistBeforePay_SurvivesRestart()
    {
        // ONE chain instance spans both hosts — the chain is the outside world and survives a server
        // restart, so its settled refund-payout count is the ground truth for "how many times the treasury
        // paid". Funded up front so a would-be double-refund can actually SETTLE and be counted — an empty
        // treasury would fault the payout and silently mask the double-pay this test exists to catch.
        var chain = new RefundProbeChain(new InMemoryChainService());
        chain.Inner.FundTreasury(50_000);
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-tournament-refund-{Guid.NewGuid():N}.db");
        try
        {
            string tid;
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IChainService>(chain);
                    // The crash: the process "dies" the instant the FIRST refund payout settles. Writes
                    // issued before that instant are on disk; writes issued after are lost with the process.
                    s.AddSingleton<IGameStatePersistence>(sp => new CrashWindowPersistence(
                        new SqliteGameStatePersistence(sp.GetRequiredService<IDbContextFactory<GameStateDbContext>>()),
                        processDied: () => chain.RefundPayoutsPaid > 0));
                });
            }))
            {
                // The dev pay-invoice endpoint hard-casts IChainService to the InMemory sim, which the probe
                // wrapper isn't — so pay the buy-ins straight into the wrapped sim (the endpoint's own call).
                var players = new List<(ArkadeHeroesClient Client, string PlayerId, string HeroId)>();
                for (var i = 0; i < 4; i++)
                {
                    var (c, dto) = await first.RegisterAsync($"TR-P{i}");
                    var heroes = await c.ClaimStartersAsync();
                    players.Add((c, dto.PlayerId, heroes[0].Id));
                }
                var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
                tid = open.Tournament.Id;
                chain.Inner.PayInvoiceFromPlayer(players[0].PlayerId, open.BuyIn.InvoiceId);
                for (var i = 1; i < 4; i++)
                {
                    var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
                    chain.Inner.PayInvoiceFromPlayer(players[i].PlayerId, join.BuyIn.InvoiceId);
                }
                first.Services.GetRequiredService<GameStore>().Heroes.TryRemove(players[1].HeroId, out _);   // strand it

                var refund = await players[0].Client.Tournament.RefundAsync(tid);
                Assert.Equal(4, refund.EntrantsRefunded);   // all four refunds settled — the crash window is live
                Assert.Equal(4, chain.RefundPayoutsPaid);
            }

            // ── restart: a fresh host and GameStore rehydrate from whatever survived the crash ──
            using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            });
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();
            var svc = restarted.Services.GetRequiredService<GameService>();

            // The `refunded` marker went durable BEFORE the first sat moved, so the restarted store must not
            // hold a live, stranded, refundable copy — heroes are never persisted, so a rehydrated bracket
            // WOULD pass the unresolvable gate and pay every buy-in back a second time.
            try { await svc.RefundTournamentAsync(tid, CancellationToken.None); }
            catch (GameRuleException) { /* expected: the bracket is terminal (filtered at load) */ }
            Assert.Equal(4, chain.RefundPayoutsPaid);   // the re-refund paid NOTHING — each buy-in came back exactly once
            if (store.Tournaments.TryGetValue(tid, out var zombie))
                Assert.Equal("refunded", zombie.Status); // if it rehydrated at all, only as the terminal marker
        }
        finally
        {
            // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
            // file is harmless either way — never fail a durability test on its own housekeeping.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    /// <summary>Delegates to the real SQLite persistence until <paramref name="processDied"/> flips, then
    /// silently drops every write — the deterministic stand-in for a process crash at that instant (mirrors
    /// DailyDurabilityGuardTests): nothing issued after the moment of death ever reaches disk.</summary>
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
    }

    /// <summary>Delegates to the real InMemory sim but counts SETTLED tournament-refund payouts (memo tag
    /// <c>tournament-refund:</c>) — the deterministic stand-in for "the sats actually left the treasury"
    /// (mirrors DailyDurabilityGuardTests' payout probe).</summary>
    private sealed class RefundProbeChain(InMemoryChainService inner) : IChainService
    {
        private int _refundPayoutsPaid;
        public InMemoryChainService Inner => inner;
        public int RefundPayoutsPaid => Volatile.Read(ref _refundPayoutsPaid);

        public async Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default)
        {
            var txId = await inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
            if (memo.StartsWith("tournament-refund:", StringComparison.Ordinal))
                Interlocked.Increment(ref _refundPayoutsPaid);
            return txId;
        }

        public Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default) => inner.MintHeroAssetAsync(toPlayerId, data, ct);
        public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);
        public Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default) => inner.RegisterPlayerAddressAsync(playerId, arkadeAddress, ct);
        public Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default) => inner.GetPlayerAddressAsync(playerId, ct);
        public Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default) => inner.GetAddressBalanceSatsAsync(playerId, ct);
        public Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default) => inner.CreateFeeInvoiceAsync(memo, amountSats, ct);
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
        public Task<OfferInfo> CreateOfferAsync(string offerId, string sellerPlayerId, string itemId, long askSats, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateOfferAsync(offerId, sellerPlayerId, itemId, askSats, refundAfterUnixSeconds, ct);
        public Task<OfferInfo> CreateHeroOfferAsync(string offerId, string sellerPlayerId, string heroAssetId, long askSats, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateHeroOfferAsync(offerId, sellerPlayerId, heroAssetId, askSats, refundAfterUnixSeconds, ct);
        public Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default) => inner.IsOfferFundedAsync(offerId, ct);
        public Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default) => inner.GetOfferParamsAsync(offerId, ct);
        public Task<string> SettleWagerEscrowAsync(string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleWagerEscrowAsync(matchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default) => inner.VerifyHeroOwnershipAsync(playerId, assetId, ct);
        public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default) => inner.GetItemAssetBalanceAsync(playerId, itemId, ct);
    }
}
