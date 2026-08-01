using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A RESOLVED bracket's outcome is durable. <c>PersistedTournament</c> used to store neither the result nor
/// the prizes on the reasoning that a resolved bracket "has already paid out", so only an UNRESOLVED one was
/// worth surviving — but paying out is exactly what makes the record matter. The sats moved, irreversibly,
/// and the only account of WHO they went to and HOW MUCH lived in RAM. A restart left a bracket that had
/// really paid a real champion real bitcoin reporting no champion, no prize, and a 404 on its replay.
///
/// The outcome is PERSISTED rather than re-derived. Re-deriving looks free — the seed is stored and
/// <c>Tournament.Resolve</c> is deterministic in (entrants, seed, config) — but the third input is not
/// recoverable: <see cref="Core.GameConfigVersion.Compute"/> is a one-way hash, so a stamped version can be
/// COMPARED and never resolved back into the <c>GameConfig</c> it names, and the server holds only today's.
/// Re-running under today's rules can name a DIFFERENT CHAMPION, not merely a different prize
/// (<c>ConfigStampReplayTests</c> hunts for exactly such a seed on purpose). For a real-money record a
/// plausible wrong winner is far worse than an honest absence, so the outcome is stored as it was paid.
/// </summary>
public class TournamentOutcomeDurabilityTests
{
    const long BuyIn = 1_000;

    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"arkade-tourney-outcome-{tag}-{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    private static WebApplicationFactory<Program> HostOn(string dbPath, IChainService chain) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.ConfigureTestServices(s => s.AddSingleton(chain));
        });

    /// <summary>Opens a 4-hero bracket, pays every buy-in, and resolves it under <paramref name="nonce"/>.</summary>
    private static async Task<string> ResolvedBracketAsync(
        WebApplicationFactory<Program> host, string tag, string nonce)
    {
        var players = new List<(ArkadeHeroesClient Client, string HeroId)>();
        for (var i = 0; i < 4; i++)
        {
            var (c, _) = await host.RegisterAsync($"{tag}-P{i}");
            players.Add((c, (await c.ClaimStartersAsync())[0].Id));
        }
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });
        }
        await players[0].Client.Tournament.ResolveAsync(tid, new FightRequest(nonce));
        return tid;
    }

    /// <summary>
    /// The whole defect in one walk: resolve a bracket, restart the server, and ask it who won and what they
    /// were paid. Both answers must survive — and the replay with them, because a champion the server names
    /// but cannot prove is exactly the claim this game's commit-reveal exists to make unnecessary.
    /// </summary>
    [Fact]
    public async Task ResolvedBracket_KeepsItsChampionPrizeAndVerifiableReplay_AcrossARestart()
    {
        var dbPath = TempDb("resolved");
        var chain = new InMemoryChainService();
        chain.FundTreasury(1_000_000);
        try
        {
            string tid;
            string championBefore;
            long prizeBefore;
            TournamentReplayDto replayBefore;
            string entrantsCommitmentBefore;
            using (var first = HostOn(dbPath, chain))
            {
                tid = await ResolvedBracketAsync(first, "Durable", "durable-nonce");
                var anonFirst = new ArkadeHeroesClient(first.CreateClient());
                var dtoBefore = await anonFirst.Tournament.GetAsync(tid);
                championBefore = dtoBefore.ChampionHeroId!;
                prizeBefore = dtoBefore.ChampionPrizeSats;
                entrantsCommitmentBefore = dtoBefore.EntrantsCommitmentHex!;
                replayBefore = await anonFirst.Tournament.ReplayAsync(tid);

                Assert.False(string.IsNullOrEmpty(championBefore));
                Assert.Equal(2_520, prizeBefore);   // 4 × 1000 pot, 10% rake → 3600 pool, 70% to the champion
            }

            // ── the restart: a fresh host and GameStore over the same database file ──
            using var restarted = HostOn(dbPath, chain);
            var anon = new ArkadeHeroesClient(restarted.CreateClient());

            var dto = await anon.Tournament.GetAsync(tid);
            Assert.Equal("resolved", dto.Status);
            Assert.Equal(championBefore, dto.ChampionHeroId);
            Assert.Equal(prizeBefore, dto.ChampionPrizeSats);
            Assert.Equal(entrantsCommitmentBefore, dto.EntrantsCommitmentHex);

            // The replay survived too — field for field, not merely "something came back".
            var replay = await anon.Tournament.ReplayAsync(tid);
            Assert.Equal(replayBefore.ChampionHeroId, replay.ChampionHeroId);
            Assert.Equal(replayBefore.Nonce, replay.Nonce);
            Assert.Equal(replayBefore.CommitmentHex, replay.CommitmentHex);
            Assert.Equal(replayBefore.ServerSeedHex, replay.ServerSeedHex);
            Assert.Equal(replayBefore.EntropyHex, replay.EntropyHex);
            Assert.Equal(replayBefore.ConfigVersion, replay.ConfigVersion);
            Assert.Equal(replayBefore.ContentVersion, replay.ContentVersion);
            Assert.Equal(replayBefore.Entrants.Count, replay.Entrants.Count);
            Assert.Equal(replayBefore.Bracket, replay.Bracket);   // TournamentMatchDto is a record — structural

            // THE ASSERTION THAT MATTERS: the rehydrated record is not just present, it still VERIFIES.
            // A stored champion nobody can recompute is a receipt with no signature on it.
            var verdict = FairnessAudit.VerifyTournament(
                tid, replay.Nonce, replay.CommitmentHex, dto.EntrantsCommitmentHex!, replay);
            Assert.True(verdict.Ok, verdict.Detail);
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>
    /// The reason resolved brackets were filtered out of the loader in the first place: a terminal bracket
    /// put back into the live store must never be able to settle a SECOND time and pay the podium twice out
    /// of a treasury that cannot print. Rehydrating the outcome is only safe because the status rides with
    /// it and both settle paths refuse a bracket that already has one — so that is what this pins, at the
    /// chain, not merely at the API.
    /// </summary>
    [Fact]
    public async Task RehydratedResolvedBracket_IsTerminal_AndCannotPayOutASecondTime()
    {
        var dbPath = TempDb("terminal");
        var chain = new InMemoryChainService();
        chain.FundTreasury(1_000_000);
        try
        {
            string tid;
            using (var first = HostOn(dbPath, chain)) tid = await ResolvedBracketAsync(first, "Terminal", "terminal-nonce");

            using var restarted = HostOn(dbPath, chain);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();
            var svc = restarted.Services.GetRequiredService<GameService>();

            // It came back — that is the fix — and it came back TERMINAL.
            Assert.True(store.Tournaments.ContainsKey(tid), "a resolved bracket must survive the restart");
            var session = store.Tournaments[tid];
            Assert.Equal("resolved", session.Status);

            var treasuryAfterRestart = await chain.TreasuryBalanceAsync();

            // Neither settle path will touch it again.
            var opener = store.Players[session.OpenerPlayerId];
            var resolveAgain = await Assert.ThrowsAsync<GameRuleException>(
                () => svc.ResolveTournamentAsync(opener, tid, "second-nonce", CancellationToken.None));
            Assert.Contains("already resolved", resolveAgain.Message);

            var refundIt = await Assert.ThrowsAsync<GameRuleException>(
                () => svc.RefundTournamentAsync(tid, opener, CancellationToken.None));
            Assert.Contains("already resolved", refundIt.Message);

            // And not one sat moved for either attempt.
            Assert.Equal(treasuryAfterRestart, await chain.TreasuryBalanceAsync());
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>
    /// The cost of making settled brackets durable, paid back.
    ///
    /// <para>Keeping the outcome means <c>store.Tournaments</c> now holds every bracket this server has ever
    /// run rather than only this process's, and <c>GET /api/tournament</c> caps its answer at 50. Tournament
    /// ids are random hex, so ordering by id is arbitrary — which makes "the newest 50" really "an arbitrary
    /// 50", and settled history can bury a player's own LIVE bracket. A bracket you cannot see is one you
    /// cannot join, resolve, or call off, and yours has real sats sitting in it.</para>
    ///
    /// <para>So the cap is made to drop HISTORY: unfinished brackets sort first. This is a regression this
    /// change would otherwise have introduced, not a pre-existing one — before it, a restart emptied the
    /// settled rows out of the store and the cap had far less to bury.</para>
    /// </summary>
    [Fact]
    public async Task ALiveBracket_StaysVisibleBehindAWallOfSettledHistory()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Visible-Alice");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, BuyIn, 4));
        await alice.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });

        // Sixty settled brackets — what a durable store hands back after a while — with ids chosen to sort
        // ABOVE any random one, which is exactly the arrangement that used to bury the live bracket.
        var store = factory.Services.GetRequiredService<GameStore>();
        for (var i = 0; i < 60; i++)
        {
            var id = $"tourney-ffffffff{i:x8}";
            store.Tournaments[id] = new TournamentSession
            {
                Id = id, OpenerPlayerId = "someone-else", BuyInSats = BuyIn, Size = 2,
                ServerSeed = [1, 2, 3], CommitmentHex = "abcd",
            };
            store.Tournaments[id].Status = "resolved";
        }

        var listed = await alice.Tournament.ListAsync();
        Assert.Contains(listed, t => t.Id == open.Tournament.Id);
    }

    /// <summary>
    /// The prize SPLIT — every rank, not just the champion's share the list DTO carries — round-trips too.
    /// <c>TournamentResult</c> is a <c>readonly record struct</c> whose <c>Matches</c> is an interface-typed
    /// list, so this is the shape most likely to come back subtly empty rather than to fail loudly: a
    /// default struct deserializes to an empty champion and a null match list without throwing. Asserted
    /// against the STORE rather than the wire because the podium split never reaches the wire outside the
    /// resolve response.
    /// </summary>
    [Fact]
    public async Task ResolvedBracket_KeepsItsFullPodiumSplitAndBracket_AcrossARestart()
    {
        var dbPath = TempDb("prizes");
        var chain = new InMemoryChainService();
        chain.FundTreasury(1_000_000);
        try
        {
            string tid;
            IReadOnlyList<long> prizesBefore;
            string championBefore;
            int foughtBefore;
            using (var first = HostOn(dbPath, chain))
            {
                tid = await ResolvedBracketAsync(first, "Split", "split-nonce");
                var before = first.Services.GetRequiredService<GameStore>().Tournaments[tid];
                prizesBefore = before.Prizes;
                championBefore = before.Result!.Value.ChampionId;
                foughtBefore = before.Result!.Value.Matches.Count(m => m.Result is not null);
                Assert.Equal(new long[] { 2_520, 1_080 }, prizesBefore.ToArray());   // 70/30 of the 3600 pool
                Assert.Equal(3, foughtBefore);                                       // 2 semis + 1 final
            }

            using var restarted = HostOn(dbPath, chain);
            _ = restarted.CreateClient();
            var after = restarted.Services.GetRequiredService<GameStore>().Tournaments[tid];

            Assert.Equal(prizesBefore.ToArray(), after.Prizes.ToArray());
            Assert.NotNull(after.Result);
            Assert.Equal(championBefore, after.Result!.Value.ChampionId);
            // Not just "a result came back" — the bracket inside it survived, byes and all.
            Assert.Equal(foughtBefore, after.Result!.Value.Matches.Count(m => m.Result is not null));
        }
        finally { CleanupDb(dbPath); }
    }
}
