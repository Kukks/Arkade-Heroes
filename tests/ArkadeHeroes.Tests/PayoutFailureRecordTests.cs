using System.Net;
using System.Text.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FailableChain = ArkadeHeroes.Tests.MoneyPathRaceGuardTests.FailableChain;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The durable record of payouts that did not complete cleanly.
///
/// <para>Three money paths — the tournament podium prize, the tournament stranded-bracket refund, and the
/// season prize — are documented as NEVER retried. Until this table the only trace of a dropped payout was
/// a log line, which rotates away and cannot be queried, so real sats owed to a NAMED player became
/// unrecoverable the moment the log aged out.</para>
///
/// <para>THE ASSERTION THAT MATTERS IS THE CLASSIFICATION, NOT THE EXISTENCE OF A ROW. A record that
/// collapsed the three outcomes into "owed" would be worse than no record at all: it would invite an
/// operator to re-pay a player whose sats have already moved. So every test below pins the outcome column
/// in BOTH directions — a failed payout must read <c>owed</c> and must NOT read <c>paid-not-booked</c>, and
/// a failed booking must read <c>paid-not-booked</c> and must NOT read <c>owed</c> — and cross-checks it
/// against what the chain says physically happened.</para>
/// </summary>
public class PayoutFailureRecordTests
{
    private const string AdminToken = "payout-failure-operator-token";
    private const long BuyIn = 1_000;

    /// <summary>A host with durability on (so there is a database to record into) over one file, driving the
    /// supplied fault-injecting chain. Starters are free so the sat totals asserted below are the payouts
    /// under test and nothing else.</summary>
    private static WebApplicationFactory<Program> HostOn(
        string dbPath, IChainService chain, Action<IServiceCollection>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:AdminToken", AdminToken);
            b.UseSetting("Game:BreedingFeeSats", "0");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton(chain);
                extra?.Invoke(s);
            });
        });

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"arkade-owed-{Guid.NewGuid():N}.db");

    private static void Cleanup(string dbPath)
    {
        // Housekeeping must never fail a test whose assertions have already passed — same reasoning as
        // AuditLogTests: SQLite pools connections, so the file may still be handled on Windows.
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Every recorded failure, read straight out of the database rather than through the paged
    /// endpoint — these assertions are about completeness, and a page size would silently truncate them.</summary>
    private static async Task<List<PersistedPayoutFailure>> AllFailuresAsync(WebApplicationFactory<Program> factory)
    {
        await using var db = await factory.Services
            .GetRequiredService<IDbContextFactory<GameStateDbContext>>().CreateDbContextAsync();
        return await db.PayoutFailures.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

    private sealed record Entrant(ArkadeHeroesClient Client, string PlayerId, string HeroId, string InvoiceId);

    /// <summary>Fills a PAID 4-player bracket. Buy-ins are cleared straight on the simulator rather than
    /// through the SDK's dev facade, so the fixture depends on nothing but the decorator's own inner chain.</summary>
    private static async Task<(List<Entrant> Entrants, string Tid)> PaidBracketAsync(
        WebApplicationFactory<Program> factory, FailableChain chain, string prefix)
    {
        var players = new List<(ArkadeHeroesClient Client, string PlayerId, string HeroId)>();
        for (var i = 0; i < 4; i++)
        {
            var (client, dto) = await factory.RegisterAsync($"{prefix}-{i}");
            var hero = (await client.ClaimStartersAsync())[0];
            players.Add((client, dto.PlayerId, hero.Id));
        }

        var entrants = new List<Entrant>();
        var open = await players[0].Client.Tournament.OpenAsync(
            new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        chain.Inner.PayInvoiceFromPlayer(players[0].PlayerId, open.BuyIn.InvoiceId);
        entrants.Add(new Entrant(players[0].Client, players[0].PlayerId, players[0].HeroId, open.BuyIn.InvoiceId));
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(
                open.Tournament.Id, new JoinTournamentRequest(players[i].HeroId));
            chain.Inner.PayInvoiceFromPlayer(players[i].PlayerId, join.BuyIn.InvoiceId);
            entrants.Add(new Entrant(players[i].Client, players[i].PlayerId, players[i].HeroId, join.BuyIn.InvoiceId));
        }
        return (entrants, open.Tournament.Id);
    }

    // ── The tournament podium prize ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The payout call fails: the sats did NOT move, so the row must say the player is OWED them.
    ///
    /// The cross-check is the load-bearing half — the chain counted exactly ONE prize settling, so the
    /// player named in this row provably did not get paid, which is what makes `owed` the true reading.
    /// </summary>
    [Fact]
    public async Task TournamentPrize_PayoutFails_RecordsTheDebtAsOwed()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            chain.Inner.FundTreasury(200_000);   // generous, so the injected fault is the ONLY way to fail
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Owed-Prize");

            chain.FailNextTournamentPrize = true;
            var resolved = await entrants[0].Client.Tournament.ResolveAsync(
                tid, new FightRequest("owed-prize-nonce"));
            Assert.Equal("resolved", resolved.Tournament.Status);

            var store = factory.Services.GetRequiredService<GameStore>();
            var champion = store.Tournaments[tid].Result!.Value.ChampionId;
            var championPlayerId = entrants.First(e => e.HeroId == champion).PlayerId;

            var row = Assert.Single(await AllFailuresAsync(factory));
            Assert.Equal(PayoutFailureOutcome.Owed, row.Outcome);
            Assert.Equal(championPlayerId, row.PlayerId);
            Assert.Equal(resolved.Prizes[0], row.AmountSats);
            Assert.Equal($"tournament:{tid}:rank1", row.PayoutTag);
            Assert.Null(row.InvoiceId);                       // a prize pays from the treasury, against no invoice
            Assert.Contains("Simulated tournament-prize fault", row.Failure);

            // The sats really did NOT move for this player: only the OTHER podium place settled. Without
            // this, "owed" would be an unverified label on a row rather than a fact about the chain.
            Assert.Equal(1, chain.TournamentPrizesPaid);

            // …and the record must not ALSO claim the money moved. The two outcomes are mutually exclusive
            // and conflating them is exactly the double-pay hazard this table exists to prevent.
            Assert.DoesNotContain(
                await AllFailuresAsync(factory), r => r.Outcome == PayoutFailureOutcome.PaidNotBooked);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// THE TRAP, and the reason this table carries an outcome column at all. The payout SUCCEEDS and the
    /// treasury book-keeping after it fails — so the catch fires THOUGH THE SATS MOVED. A record that read
    /// this as "owed" would have an operator send the same prize twice.
    ///
    /// The booking is faulted through the audit hook inside <c>GameStore.RecordOutflowAsync</c>, which is
    /// the one un-guarded await in that method and therefore the only seam that can make booking throw
    /// AFTER the payout has already settled — which is precisely the interleaving under test.
    /// </summary>
    [Fact]
    public async Task TournamentPrize_BookingFails_RecordsAsAlreadyPaid_AndNeverAsOwed()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain, s => s.AddSingleton<IAuditLog>(sp =>
                new FirstOutflowRefusingAuditLog(new SqliteAuditLog(
                    sp.GetRequiredService<IDbContextFactory<GameStateDbContext>>()))));
            chain.Inner.FundTreasury(200_000);
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Paid-Prize");

            var resolved = await entrants[0].Client.Tournament.ResolveAsync(
                tid, new FightRequest("paid-prize-nonce"));
            Assert.Equal("resolved", resolved.Tournament.Status);

            var store = factory.Services.GetRequiredService<GameStore>();
            var champion = store.Tournaments[tid].Result!.Value.ChampionId;
            var championPlayerId = entrants.First(e => e.HeroId == champion).PlayerId;

            var rows = await AllFailuresAsync(factory);
            var row = Assert.Single(rows);
            Assert.Equal(PayoutFailureOutcome.PaidNotBooked, row.Outcome);
            Assert.Equal(championPlayerId, row.PlayerId);
            Assert.Equal(resolved.Prizes[0], row.AmountSats);
            Assert.Equal($"tournament:{tid}:rank1", row.PayoutTag);

            // THE ASSERTION THIS TEST EXISTS FOR: nothing anywhere in the record says this player is owed
            // money. They have it.
            Assert.DoesNotContain(rows, r => r.Outcome == PayoutFailureOutcome.Owed);

            // And the sats provably DID move — both podium prizes settled on the chain, including the one
            // whose booking failed. That is what makes `paid-not-booked` the true reading rather than a
            // label nobody checked.
            Assert.Equal(2, chain.TournamentPrizesPaid);
        }
        finally { Cleanup(dbPath); }
    }

    // ── The tournament stranded-bracket refund ────────────────────────────────────────────────────

    /// <summary>
    /// The refund's THIRD outcome. The paid-check throws, so the server cannot tell whether this entrant's
    /// buy-in ever cleared — the debt is neither confirmed nor ruled out. Recording that as `owed` would
    /// pay back a seat that never paid; recording it as nothing at all would lose a real refund. It has to
    /// carry the INVOICE, because checking that invoice by hand is the only way to resolve it.
    /// </summary>
    [Fact]
    public async Task TournamentRefund_PaidCheckFails_RecordsUnknown_CarryingTheInvoiceToCheck()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Unknown-Refund");
            var store = factory.Services.GetRequiredService<GameStore>();
            store.Tournaments[tid].EntrantSnapshots = null;   // the strand: a full bracket that can never run

            chain.FailNextPaidCheck = true;
            var refund = await entrants[0].Client.Tournament.RefundAsync(tid);

            // The other three still got their buy-ins back — one unreadable check must not cost anyone else.
            Assert.Equal("refunded", refund.Tournament.Status);
            Assert.Equal(3, refund.EntrantsRefunded);

            var row = Assert.Single(await AllFailuresAsync(factory));
            Assert.Equal(PayoutFailureOutcome.Unknown, row.Outcome);
            Assert.Equal(BuyIn, row.AmountSats);
            Assert.StartsWith($"tournament-refund:{tid}:", row.PayoutTag);
            // The identifier that lets an operator settle it: the buy-in whose paid-state could not be read.
            Assert.Equal(entrants.First(e => e.PlayerId == row.PlayerId).InvoiceId, row.InvoiceId);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The refund payout itself fails on a buy-in that DID clear: a confirmed debt, so `owed` —
    /// and the row must carry the invoice too, since that is what proves the seat was ever paid for.</summary>
    [Fact]
    public async Task TournamentRefund_PayoutFails_RecordsTheDebtAsOwed()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Owed-Refund");
            var store = factory.Services.GetRequiredService<GameStore>();
            store.Tournaments[tid].EntrantSnapshots = null;

            chain.FailNextTournamentRefund = true;
            var refund = await entrants[0].Client.Tournament.RefundAsync(tid);
            Assert.Equal(3, refund.EntrantsRefunded);
            Assert.Equal(BuyIn * 3, refund.RefundedSats);

            var rows = await AllFailuresAsync(factory);
            var row = Assert.Single(rows);
            Assert.Equal(PayoutFailureOutcome.Owed, row.Outcome);
            Assert.Equal(BuyIn, row.AmountSats);
            Assert.Equal($"tournament-refund:{tid}:{row.PlayerId}", row.PayoutTag);
            Assert.Equal(entrants.First(e => e.PlayerId == row.PlayerId).InvoiceId, row.InvoiceId);

            // The refunded COUNT excludes this entrant, so the books and the record agree on who is short.
            Assert.DoesNotContain(rows, r => r.Outcome == PayoutFailureOutcome.PaidNotBooked);
        }
        finally { Cleanup(dbPath); }
    }

    // ── The season prize ──────────────────────────────────────────────────────────────────────────

    /// <summary>The third never-retried path. Nothing could fault a season payout before — the original
    /// seam only matched `wager-pot:`/`squad-pot:` memos — so this is the first time the season's dropped
    /// prize has been observed at all, let alone recorded.</summary>
    [Fact]
    public async Task SeasonPrize_PayoutFails_RecordsTheDebtAsOwed()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            var (alice, aliceDto) = await factory.RegisterAsync("Owed-Season-A");
            var (bob, bobDto) = await factory.RegisterAsync("Owed-Season-B");
            var ah = (await alice.ClaimStartersAsync())[0];
            var bh = (await bob.ClaimStartersAsync())[0];
            chain.Inner.FundTreasury(500_000);

            // Both heroes hold XP: a staked fight between two heroes owning nothing moves nothing, so
            // neither would record a WIN — and a season prize needs a win behind it.
            var store = factory.Services.GetRequiredService<GameStore>();
            store.Heroes[ah.Id].Xp = 500;
            store.Heroes[bh.Id].Xp = 500;

            var open = await alice.Matches.OpenAsync(new OpenMatchRequest(ah.Id, bh.Id, 1000));
            chain.Inner.StakeEscrowFromPlayer(aliceDto.PlayerId, open.MatchId);
            chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.MatchFeeInvoice!.InvoiceId);
            var acc = await bob.Matches.AcceptAsync(open.MatchId);
            chain.Inner.StakeEscrowFromPlayer(bobDto.PlayerId, open.MatchId);
            chain.Inner.PayInvoiceFromPlayer(bobDto.PlayerId, acc.MatchFeeInvoice!.InvoiceId);
            await alice.Matches.FightAsync(open.MatchId, new FightRequest("owed-season-fight"));

            using var scope = factory.Services.CreateScope();
            var game = scope.ServiceProvider.GetRequiredService<GameService>();
            var futureNow = Season.Current(DateTimeOffset.UtcNow, 14).End.AddDays(1);
            var season = Season.Current(DateTimeOffset.UtcNow, 14).Number;

            chain.FailNextSeasonPrize = true;
            var board = await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);

            var settled = board.LastSettlement;
            Assert.NotNull(settled);
            var winner = Assert.Single(settled!.Winners);

            var row = Assert.Single(await AllFailuresAsync(factory));
            Assert.Equal(PayoutFailureOutcome.Owed, row.Outcome);
            Assert.Equal(winner.AwardSats, row.AmountSats);
            Assert.Equal($"season:{season}:rank{winner.Rank}", row.PayoutTag);
            Assert.Null(row.InvoiceId);

            // The champion's own player id, so the debt names somebody who can be paid.
            Assert.Equal(store.Heroes.Values.First(h => h.Name == winner.Name).OwnerId, row.PlayerId);
        }
        finally { Cleanup(dbPath); }
    }

    // ── Durability and the read surface ───────────────────────────────────────────────────────────

    /// <summary>THE WHOLE POINT: a log line does not survive a restart and cannot be queried. This one does
    /// and can — read back through a SECOND host over the same file, with the first one disposed.</summary>
    [Fact]
    public async Task RecordedDebt_SurvivesARestart()
    {
        var dbPath = NewDbPath();
        try
        {
            string tid;
            string championPlayerId;
            long prize;
            {
                var chain = new FailableChain(new InMemoryChainService());
                using var first = HostOn(dbPath, chain);
                chain.Inner.FundTreasury(200_000);
                var (entrants, id) = await PaidBracketAsync(first, chain, "Restart-Owed");
                tid = id;
                chain.FailNextTournamentPrize = true;
                var resolved = await entrants[0].Client.Tournament.ResolveAsync(
                    tid, new FightRequest("restart-owed-nonce"));
                prize = resolved.Prizes[0];
                var store = first.Services.GetRequiredService<GameStore>();
                var champion = store.Tournaments[tid].Result!.Value.ChampionId;
                championPlayerId = entrants.First(e => e.HeroId == champion).PlayerId;
                Assert.Single(await AllFailuresAsync(first));
            }   // the server that recorded it is gone — this is the restart

            var afterChain = new FailableChain(new InMemoryChainService());
            using var second = HostOn(dbPath, afterChain);
            var row = Assert.Single(await AllFailuresAsync(second));
            Assert.Equal(PayoutFailureOutcome.Owed, row.Outcome);
            Assert.Equal(championPlayerId, row.PlayerId);
            Assert.Equal(prize, row.AmountSats);
            Assert.Equal($"tournament:{tid}:rank1", row.PayoutTag);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The operator's way in. The database is the source of truth, but it lives on a volume inside
    /// a container — an endpoint is the difference between the record existing and somebody being able to
    /// act on it. Admin-gated, and filterable by the one column that matters.</summary>
    [Fact]
    public async Task AdminEndpoint_ServesTheRecordedFailures_AndFiltersByOutcome()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            chain.Inner.FundTreasury(200_000);
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Endpoint-Owed");
            chain.FailNextTournamentPrize = true;
            await entrants[0].Client.Tournament.ResolveAsync(tid, new FightRequest("endpoint-owed-nonce"));

            var http = factory.CreateClient();
            http.DefaultRequestHeaders.Add(AdminApiContract.TokenHeader, AdminToken);

            var page = JsonDocument.Parse(await http.GetStringAsync("/api/admin/payout-failures"));
            var failures = page.RootElement.GetProperty("failures");
            Assert.Equal(1, failures.GetArrayLength());
            var only = failures[0];
            Assert.Equal(PayoutFailureOutcome.Owed, only.GetProperty("outcome").GetString());
            Assert.Equal($"tournament:{tid}:rank1", only.GetProperty("payoutTag").GetString());
            Assert.True(only.GetProperty("amountSats").GetInt64() > 0);
            Assert.False(string.IsNullOrWhiteSpace(only.GetProperty("playerId").GetString()));

            // "What is still owed" is the query an operator arrives with, so it has to be one request.
            var owed = JsonDocument.Parse(
                await http.GetStringAsync($"/api/admin/payout-failures?outcome={PayoutFailureOutcome.Owed}"));
            Assert.Equal(1, owed.RootElement.GetProperty("failures").GetArrayLength());
            var paid = JsonDocument.Parse(await http.GetStringAsync(
                $"/api/admin/payout-failures?outcome={PayoutFailureOutcome.PaidNotBooked}"));
            Assert.Equal(0, paid.RootElement.GetProperty("failures").GetArrayLength());

            // It names players and amounts, so it is as sensitive as the audit log and gated the same way.
            var anonymous = await factory.CreateClient().GetAsync("/api/admin/payout-failures");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The same read through the SDK. Separate from the raw-JSON test above because that one never
    /// reads the id or the cursor, so swapping the two adjacent longs passes it and fails this.</summary>
    [Fact]
    public async Task TheSdkReadsTheDebt_WithEveryFieldPopulated()
    {
        var dbPath = NewDbPath();
        var chain = new FailableChain(new InMemoryChainService());
        try
        {
            using var factory = HostOn(dbPath, chain);
            chain.Inner.FundTreasury(200_000);
            var (entrants, tid) = await PaidBracketAsync(factory, chain, "Sdk-Owed");
            chain.FailNextTournamentPrize = true;
            await entrants[0].Client.Tournament.ResolveAsync(tid, new FightRequest("sdk-owed-nonce"));

            var page = await entrants[0].Client.Admin.PayoutFailuresAsync(AdminToken);

            var row = Assert.Single(page.Failures);
            Assert.Equal(PayoutFailureOutcome.Owed, row.Outcome);
            Assert.Equal($"tournament:{tid}:rank1", row.PayoutTag);
            Assert.True(row.AmountSats > 0);
            Assert.False(string.IsNullOrWhiteSpace(row.PlayerId));
            Assert.True(row.AtUnixSeconds > 0);
            Assert.Equal(row.Id, page.NextAfter);

            Assert.Empty((await entrants[0].Client.Admin.PayoutFailuresAsync(
                AdminToken, outcome: PayoutFailureOutcome.PaidNotBooked)).Failures);
            Assert.Empty((await entrants[0].Client.Admin.PayoutFailuresAsync(
                AdminToken, player: "nobody-by-that-name")).Failures);
            Assert.Empty((await entrants[0].Client.Admin.PayoutFailuresAsync(
                AdminToken, after: row.Id)).Failures);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The opt-in seam. With no <c>Game:StateDbPath</c> there is no database to record into, and
    /// the flow must behave exactly as it did before this table existed — a dropped prize is logged and the
    /// rest of the podium still pays. The record must never become a NEW way for a payout path to fail.</summary>
    [Fact]
    public async Task WithNoDatabase_TheFlowIsUnchanged_AndNothingIsRecorded()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:BreedingFeeSats", "0");
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
        });
        chain.Inner.FundTreasury(200_000);
        var (entrants, tid) = await PaidBracketAsync(factory, chain, "NoDb-Owed");

        chain.FailNextTournamentPrize = true;
        var resolved = await entrants[0].Client.Tournament.ResolveAsync(tid, new FightRequest("nodb-nonce"));

        Assert.Equal("resolved", resolved.Tournament.Status);
        Assert.Equal(1, chain.TournamentPrizesPaid);   // the other place still paid — no new failure mode
        Assert.IsType<NullPayoutFailureLog>(factory.Services.GetRequiredService<IPayoutFailureLog>());
    }

    // ── Seams ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses the FIRST treasury OUTFLOW and delegates everything else to the real log.
    ///
    /// <c>GameStore.RecordOutflowAsync</c> awaits the audit write without a guard around it, so this is the
    /// only way to make booking throw at the exact point the production catch was written for: after
    /// <c>PayoutAsync</c> has already settled. Scoped to ONE outflow so a single prize lands in the
    /// `paid-not-booked` state while the rest of the podium books normally.
    /// </summary>
    private sealed class FirstOutflowRefusingAuditLog(IAuditLog inner) : IAuditLog
    {
        private int _refused;

        public Task RecordAsync(AuditEntry entry)
        {
            if (entry.EventType == AuditEventType.TreasuryOutflow
                && Interlocked.Exchange(ref _refused, 1) == 0)
                throw new InvalidOperationException("Simulated outflow-booking fault (injected by test).");
            return inner.RecordAsync(entry);
        }

        public Task<IReadOnlyList<AuditEventDto>> ReadAsync(
            long afterSequence, int take, string? subjectId, string? eventType, string? actorPlayerId,
            CancellationToken ct = default)
            => inner.ReadAsync(afterSequence, take, subjectId, eventType, actorPlayerId, ct);

        public long WriteFailures => inner.WriteFailures;
    }
}
