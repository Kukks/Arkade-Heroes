using System.Net;
using System.Text.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The append-only audit log: every state-changing action the server takes, written once and never
/// touched again.
///
/// The tests that matter here are the STRUCTURAL ones, not the "does flow X log an event" ones. A list of
/// flows and their expected event types is only ever as complete as whoever wrote the list — it goes stale
/// the day a new money path lands. So the load-bearing assertions are cross-checks against numbers the
/// server keeps INDEPENDENTLY of the log: every sat the treasury booked has to appear in the log to the
/// sat, and every hero the store counted minting or burning has to have its event. A new path that moves
/// money and forgets to log is caught by arithmetic, not by a list someone remembered to update.
/// </summary>
public class AuditLogTests
{
    private const string AdminToken = "audit-operator-token";

    /// <summary>A host with durability (and therefore the audit log) switched on, over one database file.
    /// The log follows the same opt-in seam as the rest of persistence — with no <c>Game:StateDbPath</c>
    /// there is no database to append to, which is its own test below.</summary>
    private static WebApplicationFactory<Program> HostOn(string dbPath, bool dailyFaucetOpen = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:AdminToken", AdminToken);
            if (dailyFaucetOpen) b.UseSetting("Game:DailyRewardEnabled", "true");
        });

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"arkade-audit-{Guid.NewGuid():N}.db");

    private static void Cleanup(string dbPath)
    {
        // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
        // file is harmless either way — never fail on housekeeping. Both throws are caught: a still-held
        // file gives IOException, a read-only one UnauthorizedAccessException, and a test whose assertions
        // have already passed must not go red over which of the two Windows chose.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Every event in the log, read straight out of the database rather than through the paged
    /// endpoint — the assertions below are about completeness, and a page size would silently truncate them.</summary>
    private static async Task<List<PersistedAuditEvent>> AllEventsAsync(WebApplicationFactory<Program> factory)
    {
        await using var db = await factory.Services
            .GetRequiredService<IDbContextFactory<GameStateDbContext>>().CreateDbContextAsync();
        return await db.AuditEvents.AsNoTracking().Include(e => e.Subjects)
            .OrderBy(e => e.Sequence).ToListAsync();
    }

    private static long SatsIn(PersistedAuditEvent e) =>
        JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("sats").GetInt64();

    private static string? StringIn(PersistedAuditEvent e, string property) =>
        JsonDocument.Parse(e.PayloadJson).RootElement.TryGetProperty(property, out var v)
            ? v.ValueKind == JsonValueKind.Null ? null : v.GetString()
            : null;

    /// <summary>
    /// Drives a broad battery of real flows through the real API on one host, so the assertions afterwards
    /// have something substantial to be true of. Deliberately end-to-end (SDK → HTTP → service → chain
    /// emulator): a log that is only exercised by calling the logger directly proves nothing about whether
    /// the game calls it.
    /// </summary>
    private static async Task<(ArkadeHeroesClient Alice, PlayerDto AlicePlayer, ArkadeHeroesClient Bob, PlayerDto BobPlayer)>
        DriveTheEconomyAsync(WebApplicationFactory<Program> factory)
    {
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(5_000_000);   // the faucet and the pots pay out of a real balance

        var (alice, alicePlayer) = await factory.RegisterAsync("Audit-Alice");
        var (bob, bobPlayer) = await factory.RegisterAsync("Audit-Bob");

        // ── mint: the paid recruit path (quote → pay → claim), twice each ──
        var aliceHeroes = await alice.RecruitAsync(4);
        var bobHeroes = await bob.RecruitAsync(2);

        // ── breeding: commit → pay → reveal ──
        await alice.BreedAsync(aliceHeroes[0].Id, aliceHeroes[1].Id, "audit-breed-nonce");

        // ── items: invoice → pay → claim → equip → unequip ──
        await alice.BuyItemAsync("rusty-blade");
        await alice.Heroes.EquipAsync(aliceHeroes[0].Id, new EquipRequest("rusty-blade"));
        await alice.Heroes.UnequipAsync(aliceHeroes[0].Id, new UnequipRequest("Weapon"));

        // ── gauntlet: open → pay → run ──
        var gauntlet = await alice.Gauntlet.OpenAsync(aliceHeroes[0].Id);
        await alice.PayInvoiceAsync(gauntlet.FeeInvoice.InvoiceId);
        await alice.Gauntlet.RunAsync(gauntlet.GauntletId, "audit-gauntlet-nonce");

        // ── trials: open → run (free, but it changes state) ──
        var trials = await alice.Trials.OpenAsync(aliceHeroes[0].Id);
        await alice.Trials.RunAsync(trials.TrialsId, "audit-trials-nonce");

        // ── a wagered duel, invoice mode: open → accept → both stakes + both fees → fight ──
        var match = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 500, "invoice"));
        var accepted = await bob.Matches.AcceptAsync(match.MatchId);
        await alice.PayInvoiceAsync(match.StakeInvoice!.InvoiceId);
        await alice.PayInvoiceAsync(match.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accepted.StakeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accepted.MatchFeeInvoice!.InvoiceId);
        await alice.Matches.FightAsync(match.MatchId, new FightRequest("audit-fight-nonce"));

        // ── the daily faucet (a treasury OUTFLOW with a once-per-day latch) ──
        await alice.Daily.ClaimAsync();

        // ── the marketplace: list an item, then a hero ──
        await alice.BuyItemAsync("rusty-blade");
        await alice.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 2_500));
        await alice.Offers.CreateHeroAsync(new CreateHeroOfferRequest(aliceHeroes[2].Id, 4_000));

        // ── a hero rename (a treasury sink with its own request/confirm pair) ──
        var rename = await alice.Heroes.RequestRenameAsync(aliceHeroes[1].Id, new RenameHeroRequest("Audit Champion"));
        if (rename.Fee is { } renameFee) await alice.PayInvoiceAsync(renameFee.InvoiceId);
        await alice.Heroes.ConfirmRenameAsync(aliceHeroes[1].Id);

        // ── a hero transfer: the wallet moves the asset, the server verifies ──
        await alice.TransferAssetAsync(aliceHeroes[3].Id, bobPlayer.PlayerId);
        await alice.Heroes.TransferAsync(aliceHeroes[3].Id, new TransferRequest(bobPlayer.PlayerId));

        return (alice, alicePlayer, bob, bobPlayer);
    }

    // ── (a) EVERY MONEY PATH WRITES ITS EVENT ──────────────────────────────────────────────────

    /// <summary>
    /// THE STRUCTURAL CATCH, and the reason this file exists.
    ///
    /// Every sat this server accounts for passes through exactly two methods —
    /// <c>GameStore.RecordInflowAsync</c> and <c>RecordOutflowAsync</c> — because that is what feeds the
    /// economy-health card, and a money path that books neither is already a bug those tests catch. The
    /// audit log hooks BOTH, so this assertion is not "did somebody remember to log the tournament payout":
    /// it is "does the log agree, to the sat, with a total the server computed without it".
    ///
    /// A money path added tomorrow that books its treasury movement correctly gets its audit entry for
    /// free and keeps this green. One that books nothing fails the economy tests instead. There is no
    /// third option where money moves and nothing notices.
    /// </summary>
    [Fact]
    public async Task EverySatTheTreasuryBooked_AppearsInTheLog()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath, dailyFaucetOpen: true);
            await DriveTheEconomyAsync(factory);

            var store = factory.Services.GetRequiredService<GameStore>();
            var events = await AllEventsAsync(factory);

            var loggedIn = events.Where(e => e.EventType == AuditEventType.TreasuryInflow).Sum(SatsIn);
            var loggedOut = events.Where(e => e.EventType == AuditEventType.TreasuryOutflow).Sum(SatsIn);

            // The battery has to have MOVED money, or this whole assertion is vacuously true of zero.
            Assert.True(store.TreasuryInflowByTag.Values.Sum() > 0, "the battery booked no treasury income at all");
            Assert.True(store.TreasuryOutflowByTag.Values.Sum() > 0, "the battery booked no treasury payout at all");

            Assert.Equal(store.TreasuryInflowByTag.Values.Sum(), loggedIn);
            Assert.Equal(store.TreasuryOutflowByTag.Values.Sum(), loggedOut);

            // …and every booked TAG is represented, so the log can answer "what did this income consist of"
            // rather than only "how much was there".
            var loggedInflowTags = events.Where(e => e.EventType == AuditEventType.TreasuryInflow)
                .Select(e => StringIn(e, "tag")).ToHashSet();
            Assert.Empty(store.TreasuryInflowByTag.Keys.Except(loggedInflowTags!));
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// The asset side of the same catch. Heroes are minted and burned at counted choke points the store
    /// tracks on its own (<c>HeroesMinted</c>/<c>HeroesBurned</c>, incremented right beside the store
    /// mutation), so the log has to agree with those counters exactly. A future mint or burn path that
    /// skips the log has to skip the counter too — and skipping the counter breaks the economy card.
    /// </summary>
    [Fact]
    public async Task EveryHeroMintedOrBurned_HasItsEvent()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath, dailyFaucetOpen: true);
            var (alice, _, bob, _) = await DriveTheEconomyAsync(factory);

            // Burn some heroes for real: a fusion retires two inputs and mints one. FRESH recruits, never
            // a pick out of the roster — `MineAsync` enumerates a ConcurrentDictionary in arbitrary order,
            // and the battery above leaves behind a hero whose asset is escrowed in a live offer. Merging
            // that one is a different test failing for a different reason on some runs and not others.
            // (Sterility and cooldowns do not gate a merge, so fresh recruits make this fully determined.)
            var toMerge = await alice.RecruitAsync(2);
            var merge = await alice.Merge.CommitAsync(new MergeCommitRequest(toMerge[0].Id, toMerge[1].Id));
            await alice.Dev.FundMergeEscrowAsync(new { MergeId = merge.MergeId });
            await alice.Merge.RevealAsync(merge.MergeId, new MergeRevealRequest("audit-merge-nonce"));

            var store = factory.Services.GetRequiredService<GameStore>();
            var events = await AllEventsAsync(factory);

            var minted = events.Count(e => e.EventType == AuditEventType.HeroMinted);
            var burned = events.Count(e => e.EventType == AuditEventType.HeroBurned);

            Assert.True(store.HeroesMinted > 0, "the battery minted no heroes at all");
            Assert.True(store.HeroesBurned > 0, "the fusion burned no heroes at all");
            Assert.Equal(store.HeroesMinted, minted);
            Assert.Equal(store.HeroesBurned, burned);

            // Every hero still alive can be traced back to its own mint event — the log is complete, not
            // merely correct in aggregate.
            var mintedIds = events.Where(e => e.EventType == AuditEventType.HeroMinted)
                .Select(e => StringIn(e, "heroId")).ToHashSet();
            foreach (var heroId in store.Heroes.Keys)
                Assert.Contains(heroId, mintedIds);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// The named enumeration: the flows this repo has, and the event each one is supposed to write. It is
    /// the WEAKER of the coverage tests — a list is only as complete as its author — which is exactly why
    /// the two arithmetic cross-checks above exist beside it. What this one buys is naming: when a flow
    /// stops logging, the failure says WHICH flow rather than "the totals disagree by 500".
    /// </summary>
    [Fact]
    public async Task EveryFlowInTheGame_WritesItsNamedEvent()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath, dailyFaucetOpen: true);
            var (alice, _, bob, bobPlayer) = await DriveTheEconomyAsync(factory);

            // A few flows the shared battery leaves out because they need a second party or an escrow.
            //
            // FRESH heroes for both, never a pick out of the existing roster. `MineAsync` is backed by a
            // ConcurrentDictionary, so its order is arbitrary — and the battery above has left cooldowns on
            // the heroes it bred, an escrowed asset on the one it listed, and a burned pair behind the
            // merge. Indexing into that list picks a different hero run to run and fails whenever it lands
            // on one the flow legitimately refuses. (It did: this test flaked before it was pinned.)
            var toMerge = await alice.RecruitAsync(2);
            var merge = await alice.Merge.CommitAsync(new MergeCommitRequest(toMerge[0].Id, toMerge[1].Id));
            await alice.Dev.FundMergeEscrowAsync(new { MergeId = merge.MergeId });
            await alice.Merge.RevealAsync(merge.MergeId, new MergeRevealRequest("audit-merge-nonce"));

            // Sterility is derived from the genome and genomes are random, so no fixed pick is safe here
            // either — a sterile hero on either side is refused. Take the first pair the server ACCEPTS
            // rather than asserting one it has no reason to.
            var mine = await alice.RecruitAsync(4);
            var theirs = await bob.RecruitAsync(4);
            StudProposeResponse? stud = null;
            foreach (var m in mine)
            {
                foreach (var t in theirs)
                {
                    try { stud = await alice.Stud.ProposeAsync(new StudProposeRequest(m.Id, t.Id, 300)); }
                    catch (ArkadeHeroesApiException) { continue; }   // sterile on one side — try the next
                    break;
                }
                if (stud is not null) break;
            }
            Assert.NotNull(stud);   // 16 fresh pairs all sterile would be a broken game, not a flaky test

            var studAccepted = await bob.Stud.AcceptAsync(stud.ProposalId);
            await alice.PayInvoiceAsync(studAccepted.BreedFeeInvoice.InvoiceId);
            await alice.PayInvoiceAsync(studAccepted.StudFeeInvoice!.InvoiceId);
            await alice.Stud.RevealAsync(stud.ProposalId, new StudRevealRequest("audit-stud-nonce"));

            var events = await AllEventsAsync(factory);
            var seen = events.Select(e => e.EventType).ToHashSet();

            // The enumeration. Each line is one action this server can take that changes state or moves
            // value; if a line here has no event, the flow beside it is writing nothing.
            foreach (var expected in new[]
                     {
                         AuditEventType.PlayerRegistered,
                         AuditEventType.StarterRequested,
                         AuditEventType.StarterClaimed,
                         AuditEventType.HeroMinted,
                         AuditEventType.HeroBurned,
                         AuditEventType.HeroRenameRequested,
                         AuditEventType.HeroRenamed,
                         AuditEventType.HeroTransferred,
                         AuditEventType.HeroEquipped,
                         AuditEventType.HeroUnequipped,
                         AuditEventType.BreedCommitted,
                         AuditEventType.BreedRevealed,
                         AuditEventType.StudProposed,
                         AuditEventType.StudAccepted,
                         AuditEventType.StudRevealed,
                         AuditEventType.MergeCommitted,
                         AuditEventType.MergeRevealed,
                         AuditEventType.MatchOpened,
                         AuditEventType.MatchAccepted,
                         AuditEventType.MatchResolved,
                         AuditEventType.GauntletOpened,
                         AuditEventType.GauntletRun,
                         AuditEventType.TrialsOpened,
                         AuditEventType.TrialsRun,
                         AuditEventType.ItemInvoiced,
                         AuditEventType.ItemClaimed,
                         AuditEventType.OfferListed,
                         AuditEventType.DailyClaimed,
                         AuditEventType.TreasuryInflow,
                         AuditEventType.TreasuryOutflow,
                     })
                Assert.True(seen.Contains(expected), $"no '{expected}' event was ever written");
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The permadeath flow and the buy-in bracket, which need their own hosts to set up cleanly.
    /// Both are money paths: one burns an asset, the other pays a pot out of the treasury.</summary>
    [Fact]
    public async Task TheDeathMatchAndTournamentFlows_WriteTheirEvents()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
            chain.FundTreasury(1_000_000);

            var (alice, alicePlayer) = await factory.RegisterAsync("Audit-DM-Alice");
            var (bob, bobPlayer) = await factory.RegisterAsync("Audit-DM-Bob");
            var aliceHeroes = await alice.RecruitAsync(2);
            var bobHeroes = await bob.RecruitAsync(2);

            // ── death-match: open → accept → both stake → both fees → settle (the loser BURNS) ──
            var dm = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
            var dmAccepted = await bob.DeathMatch.AcceptAsync(dm.DeathMatchId);
            await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = dm.DeathMatchId, Role = "challenger" });
            await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = dm.DeathMatchId, Role = "defender" });
            await alice.PayInvoiceAsync(dm.FeeInvoice!.InvoiceId);
            await bob.PayInvoiceAsync(dmAccepted.FeeInvoice!.InvoiceId);
            await alice.DeathMatch.SettleAsync(dm.DeathMatchId, new DeathMatchSettleRequest("audit-dm-nonce"));

            // ── tournament: open → join → both buy-ins paid → resolve (the podium is PAID) ──
            var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHeroes[1].Id, 400, 2));
            var joined = await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHeroes[1].Id));
            await alice.PayInvoiceAsync(open.BuyIn.InvoiceId);
            await bob.PayInvoiceAsync(joined.BuyIn.InvoiceId);
            await alice.Tournament.ResolveAsync(open.Tournament.Id, new FightRequest("audit-tourney-nonce"));

            var seen = (await AllEventsAsync(factory)).Select(e => e.EventType).ToHashSet();
            foreach (var expected in new[]
                     {
                         AuditEventType.DeathMatchOpened,
                         AuditEventType.DeathMatchAccepted,
                         AuditEventType.DeathMatchSettled,
                         AuditEventType.HeroBurned,
                         AuditEventType.TournamentOpened,
                         AuditEventType.TournamentJoined,
                         AuditEventType.TournamentResolved,
                     })
                Assert.True(seen.Contains(expected), $"no '{expected}' event was ever written");
        }
        finally { Cleanup(dbPath); }
    }

    // ── (b) A RETRIED ACTION DOES NOT DOUBLE-LOG ───────────────────────────────────────────────

    /// <summary>
    /// Clients poll-retry every one of these flows, so "logged once per ACTION" and "logged once per CALL"
    /// are very different properties and only the first is useful.
    ///
    /// NOTE ON WHAT THIS PROVES, because it is less than it looks. For the three flows below the second
    /// call never reaches the log at all: the item claim returns early on its <c>claimed</c> status, and
    /// the daily and gauntlet retries are refused outright by their own latches. So what is under test
    /// here is that a retry does not produce a second entry — which is the property that matters — but the
    /// mechanism doing the work is the FLOW's once-only guard, not the log's dedup key. The log's own key
    /// is exercised directly at the end of this test, and on a real flow in
    /// <see cref="TheSameCloseProvenTwice_IsLoggedOnce"/>, which is where it does work nothing else does.
    /// </summary>
    [Fact]
    public async Task ARetriedAction_IsLoggedExactlyOnce()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath, dailyFaucetOpen: true);
            var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
            chain.FundTreasury(1_000_000);

            var (alice, alicePlayer) = await factory.RegisterAsync("Audit-Retry");
            var heroes = await alice.RecruitAsync(2);

            // An item claim is IDEMPOTENTLY SUCCESSFUL on retry — it re-reports the delivery rather than
            // throwing — which makes it the sharpest case: the second call returns 200 and must still not
            // produce a second entry.
            var invoice = (await alice.Items.BuyAsync("rusty-blade")).Invoice;
            await alice.PayInvoiceAsync(invoice.InvoiceId);
            await alice.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
            await alice.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));   // the retry
            await alice.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));   // and again

            // The daily claim: a second attempt is refused, and must leave exactly one entry behind.
            await alice.Daily.ClaimAsync();
            await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Daily.ClaimAsync());

            // A gauntlet run: the session's Completed latch refuses the second call.
            var gauntlet = await alice.Gauntlet.OpenAsync(heroes[0].Id);
            await alice.PayInvoiceAsync(gauntlet.FeeInvoice.InvoiceId);
            await alice.Gauntlet.RunAsync(gauntlet.GauntletId, "retry-nonce");
            await Assert.ThrowsAsync<ArkadeHeroesApiException>(
                () => alice.Gauntlet.RunAsync(gauntlet.GauntletId, "retry-nonce"));

            var events = await AllEventsAsync(factory);
            Assert.Single(events.Where(e => e.EventType == AuditEventType.ItemClaimed));
            Assert.Single(events.Where(e => e.EventType == AuditEventType.DailyClaimed));
            Assert.Single(events.Where(e => e.EventType == AuditEventType.GauntletRun));

            // The treasury side too: the item's price is booked once, so it is logged once — three claims,
            // one inflow entry for that invoice.
            Assert.Single(events.Where(e => e.EventType == AuditEventType.TreasuryInflow
                                            && e.DedupKey == $"treasury-in:{invoice.InvoiceId}"));

            // And the guarantee is in the SCHEMA, not merely in the flows: a direct second write under a
            // key already used is refused by the unique index and absorbed, not appended.
            var audit = factory.Services.GetRequiredService<IAuditLog>();
            var before = (await AllEventsAsync(factory)).Count;
            await audit.RecordAsync(new AuditEntry(
                AuditEventType.DailyClaimed, alicePlayer.PlayerId, [alicePlayer.PlayerId],
                new { forged = true },
                events.First(e => e.EventType == AuditEventType.DailyClaimed).DedupKey));
            Assert.Equal(before, (await AllEventsAsync(factory)).Count);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// THE DEDUP KEY DOING WORK NOTHING ELSE DOES — the case the retry test above cannot reach.
    ///
    /// One offer's close is provable two independent ways, and BOTH can run: reconcile observes the asset
    /// left the covenant and the treasury took its cut, and the buyer's claim observes the chain showing
    /// them holding the hero. Neither path checks the other's status first — <c>ClaimPurchasedHeroAsync</c>
    /// deliberately does not refuse an offer reconcile has already closed — so the second one through
    /// genuinely reaches the log with the same fact. Only the shared once-only key stops the close being
    /// recorded twice, and this is the same key discipline <c>OfferSaleInflowId</c> uses to stop the FEE
    /// being booked twice off exactly this race.
    /// </summary>
    [Fact]
    public async Task TheSameCloseProvenTwice_IsLoggedOnce()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (seller, sellerPlayer) = await factory.RegisterAsync("Audit-Close-Seller");
            var (buyer, buyerPlayer) = await factory.RegisterAsync("Audit-Close-Buyer");
            var heroes = await seller.RecruitAsync(2);

            var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroes[0].Id, 6_000));
            await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
            await seller.Offers.ListAsync();                                  // observed funded → active
            await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });

            // PROOF ONE: reconcile sees the asset gone and the treasury paid — it closes the offer.
            await buyer.Offers.ListAsync();
            // PROOF TWO: the buyer claims game-side ownership, which knows the sale happened AND who
            // bought it. It writes the same close again, under the same key.
            await buyer.Offers.ClaimHeroAsync(offer.OfferId);

            var closes = (await AllEventsAsync(factory))
                .Where(e => e.EventType == AuditEventType.OfferClosed
                            && e.Subjects.Any(s => s.SubjectId == offer.OfferId))
                .ToList();
            Assert.Single(closes);

            // Both proofs really did run — otherwise the single close above would be single for the boring
            // reason that only one path ever fired, and this test would prove nothing.
            Assert.Contains(await AllEventsAsync(factory),
                e => e.EventType == AuditEventType.OfferHeroClaimed
                     && e.Subjects.Any(s => s.SubjectId == offer.OfferId));
            Assert.Equal(buyerPlayer.PlayerId,
                (await buyer.Heroes.GetAsync(heroes[0].Id)).OwnerId);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// The dedup index really is a UNIQUE constraint, and it really does report SQLITE_CONSTRAINT_UNIQUE.
    ///
    /// This pins the ONE magic number in <c>SqliteAuditLog</c>. The lookup in front of the insert catches
    /// the ordinary retry, so the catch behind it only ever fires on a genuine race — which no test can
    /// schedule reliably, and which would therefore ship unverified. Getting the code wrong is not
    /// catastrophic (a race would be counted as a write failure it isn't) but it is silent, and silent is
    /// exactly what the failure counter exists to prevent.
    ///
    /// Deliberately checks the EXTENDED code. The primary code is shared by every constraint in the
    /// schema, so matching on it would let a genuinely broken write be absorbed as a benign duplicate.
    /// </summary>
    [Fact]
    public async Task ADuplicateDedupKey_RaisesTheExactConstraintTheLogAbsorbs()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (alice, _) = await factory.RegisterAsync("Audit-Constraint");
            await alice.RecruitAsync(2);

            var existing = (await AllEventsAsync(factory)).First(e => e.DedupKey is not null);

            // Straight at the table, bypassing RecordAsync's lookup — the only way to reach the constraint.
            await using var db = await factory.Services
                .GetRequiredService<IDbContextFactory<GameStateDbContext>>().CreateDbContextAsync();
            db.AuditEvents.Add(new PersistedAuditEvent
            {
                AtUtc = DateTimeOffset.UtcNow,
                EventType = "probe.duplicate",
                PayloadJson = "{}",
                DedupKey = existing.DedupKey,
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var sqlite = Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(ex.InnerException);
            Assert.Equal(2067, sqlite.SqliteExtendedErrorCode);   // SQLITE_CONSTRAINT_UNIQUE

            // And the log itself absorbs a repeat of a written key without counting a failure.
            var audit = factory.Services.GetRequiredService<IAuditLog>();
            var before = (await AllEventsAsync(factory)).Count;
            await audit.RecordAsync(new AuditEntry(
                existing.EventType, existing.ActorPlayerId, [], new { repeat = true }, existing.DedupKey));
            Assert.Equal(before, (await AllEventsAsync(factory)).Count);
            Assert.Equal(0, audit.WriteFailures);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// The other half of dedup: where an action can genuinely RECUR, the log must not collapse the
    /// repetitions. Two identical payouts are two facts — the treasury ledger deliberately refuses to
    /// dedup them for exactly this reason, and a log that silently merged them would under-report a real
    /// sat movement while looking tidy.
    /// </summary>
    [Fact]
    public async Task TwoIdenticalPayouts_AreLoggedTwice()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            _ = factory.CreateClient();
            var store = factory.Services.GetRequiredService<GameStore>();

            await store.RecordOutflowAsync("daily", 100);
            await store.RecordOutflowAsync("daily", 100);   // same tag, same amount, a second real payout

            var outflows = (await AllEventsAsync(factory))
                .Where(e => e.EventType == AuditEventType.TreasuryOutflow).ToList();
            Assert.Equal(2, outflows.Count);
            Assert.Equal(200, outflows.Sum(SatsIn));
        }
        finally { Cleanup(dbPath); }
    }

    // ── (c) THE LOG SURVIVES A RESTART ─────────────────────────────────────────────────────────

    /// <summary>
    /// A real restart: a second host, a fresh <see cref="GameStore"/>, the same database file. History has
    /// to still be there, and the sequence has to keep climbing from where it stopped rather than
    /// restarting at 1 and interleaving new events with old ones under the same numbers.
    /// </summary>
    [Fact]
    public async Task TheLog_SurvivesARestart_AndKeepsCountingUp()
    {
        var dbPath = NewDbPath();
        try
        {
            List<PersistedAuditEvent> before;
            string heroId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Audit-Restart");
                heroId = (await alice.RecruitAsync(2))[0].Id;
                before = await AllEventsAsync(first);
                Assert.NotEmpty(before);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs

            var after = await AllEventsAsync(restarted);
            Assert.Equal(before.Count, after.Count);
            // Not merely "the same number of rows" — the same rows, byte for byte in the fields that matter.
            Assert.Equal(
                before.Select(e => (e.Sequence, e.EventType, e.PayloadJson, e.ActorPlayerId)).ToList(),
                after.Select(e => (e.Sequence, e.EventType, e.PayloadJson, e.ActorPlayerId)).ToList());
            Assert.Contains(after, e => e.EventType == AuditEventType.HeroMinted
                                        && e.Subjects.Any(s => s.SubjectId == heroId));

            // New activity on the restarted host continues the SAME log rather than starting a second one.
            var (bob, _) = await restarted.RegisterAsync("Audit-Restart-Bob");
            await bob.RecruitAsync(2);
            var grown = await AllEventsAsync(restarted);
            Assert.True(grown.Count > after.Count);
            Assert.True(grown.Skip(after.Count).All(e => e.Sequence > after[^1].Sequence),
                "the sequence must keep climbing across a restart — a reused number would re-order history");
            // Strictly increasing throughout, which is what makes the sequence a usable paging cursor.
            Assert.Equal(grown.Select(e => e.Sequence).OrderBy(s => s).ToList(), grown.Select(e => e.Sequence).ToList());
            Assert.Equal(grown.Select(e => e.Sequence).Distinct().Count(), grown.Count);
        }
        finally { Cleanup(dbPath); }
    }

    // ── (d) EVENTS ARE NEVER MUTATED ───────────────────────────────────────────────────────────

    /// <summary>
    /// Append-only, ENFORCED BY THE DATABASE. The C# surface has no update or delete method, but that is a
    /// convention and a convention is what an audit log cannot rest on — the value of the record is that it
    /// still says what it said at the time. So the migration installs BEFORE UPDATE and BEFORE DELETE
    /// triggers, and this test goes AROUND the application entirely: raw SQL, straight at the table, of the
    /// kind a compromised process or a careless migration would run. Both are refused, and the row is
    /// untouched afterwards.
    /// </summary>
    [Fact]
    public async Task ARecordedEvent_CanNeitherBeUpdatedNorDeleted()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (alice, _) = await factory.RegisterAsync("Audit-Immutable");
            await alice.RecruitAsync(2);

            var original = (await AllEventsAsync(factory)).First(e => e.EventType == AuditEventType.HeroMinted);

            await using var db = await factory.Services
                .GetRequiredService<IDbContextFactory<GameStateDbContext>>().CreateDbContextAsync();

            var update = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync(
                    "UPDATE AuditEvents SET PayloadJson = 'tampered' WHERE Sequence = @seq",
                    new Microsoft.Data.Sqlite.SqliteParameter("@seq", original.Sequence)));
            Assert.Contains("append-only", update.Message);

            // Even the actor — the "who did this" field, the single most tempting thing to rewrite.
            var reattribute = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync(
                    "UPDATE AuditEvents SET ActorPlayerId = 'somebody-else' WHERE Sequence = @seq",
                    new Microsoft.Data.Sqlite.SqliteParameter("@seq", original.Sequence)));
            Assert.Contains("append-only", reattribute.Message);

            var delete = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents WHERE Sequence = @seq",
                    new Microsoft.Data.Sqlite.SqliteParameter("@seq", original.Sequence)));
            Assert.Contains("append-only", delete.Message);

            // …and a blanket wipe, which is what somebody covering their tracks would actually reach for.
            var wipe = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents"));
            Assert.Contains("append-only", wipe.Message);

            // The subject rows are protected the same way — an event whose subjects could be edited is an
            // event whose "which hero did this touch" answer is editable, which is the same hole.
            var subjects = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEventSubjects WHERE Sequence = @seq",
                    new Microsoft.Data.Sqlite.SqliteParameter("@seq", original.Sequence)));
            Assert.Contains("append-only", subjects.Message);

            // Nothing moved.
            var after = (await AllEventsAsync(factory)).First(e => e.Sequence == original.Sequence);
            Assert.Equal(original.PayloadJson, after.PayloadJson);
            Assert.Equal(original.EventType, after.EventType);
            Assert.Equal(original.Subjects.Count, after.Subjects.Count);
        }
        finally { Cleanup(dbPath); }
    }

    // ── The read surface ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The per-subject query, which is the question an operator actually arrives with: what happened to
    /// THIS hero. Its whole life — minted, geared, fought, sold — has to come back under one id, in the
    /// order it happened, however many different flows produced the entries.
    /// </summary>
    [Fact]
    public async Task OneHerosWholeHistory_ComesBackUnderItsOwnId()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (alice, alicePlayer) = await factory.RegisterAsync("Audit-Subject-Alice");
            var (bob, bobPlayer) = await factory.RegisterAsync("Audit-Subject-Bob");
            var heroes = await alice.RecruitAsync(2);
            var hero = heroes[0];

            await alice.BuyItemAsync("rusty-blade");
            await alice.Heroes.EquipAsync(hero.Id, new EquipRequest("rusty-blade"));
            await alice.TransferAssetAsync(hero.Id, bobPlayer.PlayerId);
            await alice.Heroes.TransferAsync(hero.Id, new TransferRequest(bobPlayer.PlayerId));

            var page = await alice.Admin.AuditForSubjectAsync(AdminToken, hero.Id);

            Assert.Contains(page.Events, e => e.EventType == AuditEventType.HeroMinted);
            Assert.Contains(page.Events, e => e.EventType == AuditEventType.HeroEquipped);
            Assert.Contains(page.Events, e => e.EventType == AuditEventType.HeroTransferred);
            // In append order, always — the sequence IS the history's order.
            Assert.Equal(page.Events.Select(e => e.Sequence).OrderBy(s => s).ToList(),
                page.Events.Select(e => e.Sequence).ToList());
            // And ONLY this hero's events: the sibling recruit's mint must not be in here.
            Assert.All(page.Events, e => Assert.Contains(hero.Id, e.SubjectIds));
            Assert.DoesNotContain(page.Events, e => e.SubjectIds.Contains(heroes[1].Id));

            // The transfer names both sides and the price-free reason, so custody is answerable from the
            // log alone — the hero row itself only ever holds the CURRENT owner.
            var transfer = page.Events.Last(e => e.EventType == AuditEventType.HeroTransferred);
            using var payload = JsonDocument.Parse(transfer.PayloadJson);
            Assert.Equal(alicePlayer.PlayerId, payload.RootElement.GetProperty("fromPlayerId").GetString());
            Assert.Equal(bobPlayer.PlayerId, payload.RootElement.GetProperty("toPlayerId").GetString());
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>Paging on the sequence cursor: walking with <c>after</c> visits every event exactly once,
    /// which is the property a poller depends on and the reason the cursor is exclusive.</summary>
    [Fact]
    public async Task PagingOnTheCursor_VisitsEveryEventExactlyOnce()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (alice, _) = await factory.RegisterAsync("Audit-Paging");
            await alice.RecruitAsync(4);

            var all = await AllEventsAsync(factory);
            Assert.True(all.Count > 5, "the walk needs more events than one page to be worth anything");

            var walked = new List<long>();
            long after = 0;
            while (true)
            {
                var page = await alice.Admin.AuditAsync(AdminToken, after, take: 2);
                if (page.Events.Count == 0) break;
                walked.AddRange(page.Events.Select(e => e.Sequence));
                // The cursor MUST advance. A non-advancing NextAfter would re-serve the same page forever
                // and this test would hang rather than fail — and a hung test blocks the whole suite, which
                // is a worse failure than the one it was written to catch.
                Assert.True(page.NextAfter > after,
                    $"the paging cursor did not advance past {after} — the walk would never terminate");
                after = page.NextAfter;
            }

            Assert.Equal(all.Select(e => e.Sequence).ToList(), walked);
            Assert.Equal(walked.Distinct().Count(), walked.Count);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>The log names every player, every amount and every counterparty in the game, so it is the
    /// most revealing read on the server. It must be behind the admin gate, and it must not exist at all on
    /// a deployment that configured no operator token.</summary>
    [Fact]
    public async Task TheAuditRead_IsGated()
    {
        var dbPath = NewDbPath();
        try
        {
            using var gated = HostOn(dbPath);
            var (alice, _) = await gated.RegisterAsync("Audit-Gate");
            await alice.RecruitAsync(2);

            foreach (var path in new[] { "/api/admin/audit", "/api/admin/audit/subjects/anything" })
            {
                // No token, and a wrong token, both refused — never 200.
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await gated.CreateClient().GetAsync(path)).StatusCode);

                using var wrong = new HttpRequestMessage(HttpMethod.Get, path);
                wrong.Headers.Add(AdminApiContract.TokenHeader, "not-the-token");
                Assert.Equal(HttpStatusCode.Unauthorized, (await gated.CreateClient().SendAsync(wrong)).StatusCode);
            }

            // With the right token it answers — so the refusals above are the gate, not a broken route.
            Assert.NotEmpty((await alice.Admin.AuditAsync(AdminToken)).Events);
        }
        finally { Cleanup(dbPath); }
    }

    // ── The failure policy ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FAIL POLICY, tested rather than merely documented.
    ///
    /// A log write that fails must NOT throw. These calls sit inside money paths whose catch blocks unwind
    /// in-memory state — a throw out of the daily claim's log would restore <c>LastClaimDay</c> over a
    /// durable consume and let the same day be paid twice. So the write is best-effort, and what "must not
    /// silently fail" means is that the failure is COUNTED: <c>WriteFailures</c> is the number an operator
    /// reads to know the log has gone deaf.
    /// </summary>
    [Fact]
    public async Task AFailedWrite_IsCountedRatherThanThrown()
    {
        // A database path that cannot exist: a file inside a directory that isn't there.
        var impossible = Path.Combine(Path.GetTempPath(), $"arkade-audit-missing-{Guid.NewGuid():N}", "nope.db");
        var services = new ServiceCollection()
            .AddDbContextFactory<GameStateDbContext>(o => o.UseSqlite($"Data Source={impossible}"))
            .BuildServiceProvider();

        var audit = new SqliteAuditLog(services.GetRequiredService<IDbContextFactory<GameStateDbContext>>());
        Assert.Equal(0, audit.WriteFailures);

        // The call itself must complete. This is the whole policy: a broken log cannot abort a settled payout.
        await audit.RecordAsync(new AuditEntry(
            AuditEventType.TreasuryOutflow, null, ["player-1"], new { sats = 500 }));

        Assert.Equal(1, audit.WriteFailures);

        // …and it keeps counting, so the operator sees a rate rather than a single stuck flag.
        await audit.RecordAsync(new AuditEntry(
            AuditEventType.TreasuryOutflow, null, ["player-1"], new { sats = 500 }));
        Assert.Equal(2, audit.WriteFailures);
    }

    /// <summary>The failure count is surfaced on the read itself, which is the only place an operator would
    /// look — a warning in a log file nobody greps is not observability.</summary>
    [Fact]
    public async Task TheWriteFailureCount_IsServedBesideTheEvents()
    {
        var dbPath = NewDbPath();
        try
        {
            using var factory = HostOn(dbPath);
            var (alice, _) = await factory.RegisterAsync("Audit-Failures");
            var page = await alice.Admin.AuditAsync(AdminToken);
            Assert.Equal(0, page.WriteFailures);   // a healthy server reports zero, which is the baseline
            Assert.Equal(factory.Services.GetRequiredService<IAuditLog>().WriteFailures, page.WriteFailures);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>
    /// With no <c>Game:StateDbPath</c> the server keeps its historical all-in-memory behaviour, and there is
    /// no database for the log to append to. It must degrade to a no-op rather than throwing or half-working
    /// — every other test in this suite runs on that configuration.
    /// </summary>
    [Fact]
    public async Task WithNoDurabilityConfigured_TheLogIsANoOp()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Game:AdminToken", AdminToken));

        var (alice, _) = await factory.RegisterAsync("Audit-Ephemeral");
        await alice.RecruitAsync(2);   // a full money path, on a server with nowhere to log it

        var audit = factory.Services.GetRequiredService<IAuditLog>();
        Assert.IsType<NullAuditLog>(audit);
        Assert.Equal(0, audit.WriteFailures);   // a no-op is not a failure
        Assert.Empty((await alice.Admin.AuditAsync(AdminToken)).Events);
    }
}
