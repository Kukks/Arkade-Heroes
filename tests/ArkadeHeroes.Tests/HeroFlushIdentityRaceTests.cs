using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The flush-vs-identity write race. The periodic hero flush snapshots a LIVE hero it does not lock, so
/// its save can interleave with the inline identity saves (transfer, rename) that run concurrently in
/// requests — and whichever commit lands LAST is the durable word. These tests stretch the flush's real
/// load-snapshot-commit window deterministically (an EF interceptor holds its SaveChanges open — the same
/// gap the scheduler and SQLite's write queue open nondeterministically under load) and interleave real
/// inline saves inside it, then prove what a restart rehydrates. Everything on the persistence side is
/// production code: the real SqliteGameStatePersistence on both sides of the race, the real
/// HeroFlushService, the real boot-time rehydrate.
/// </summary>
public class HeroFlushIdentityRaceTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"arkade-flush-race-{tag}-{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
        // file is harmless either way — never fail a durability test on its own housekeeping.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    private static PooledDbContextFactory<GameStateDbContext> FactoryOn(string dbPath, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<GameStateDbContext>()
            .UseSqlite($"Data Source={dbPath}").AddInterceptors(interceptors).Options);

    /// <summary>Holds ONE armed SaveChanges open between the save's field-copy and its commit — the flush's
    /// real in-method gap, stretched wide enough for a test to land inline saves inside it. Unarmed saves
    /// pass straight through.</summary>
    private sealed class HoldSaveOpenGate : SaveChangesInterceptor
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        /// <summary>Completes when the armed save has copied its snapshot and is held before committing.</summary>
        public Task Paused => _paused.Task;
        public void Arm() => _armed = true;
        public void Release() => _release.Release();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                _paused.TrySetResult();
                await _release.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    /// <summary>
    /// The catastrophic interleaving: a hero already dirty from grinding is transferred TWICE while one
    /// flush save is in flight — the flush loads the row (owner still the seller), copies the live hero
    /// mid-first-transfer (owner = buyer), and its commit lands after the second transfer's inline save
    /// (owner = final recipient). A full-surface flush write then reverts the durable owner to the
    /// INTERMEDIATE holder, and the restart rehydrates a hero the server believes belongs to someone the
    /// chain shows no longer holds it — exactly the mis-own the PersistedHero contract forbids.
    /// </summary>
    [Fact]
    public async Task FlushCommittingAcrossTwoTransfers_MustNotRevertTheDurableOwner()
    {
        var dbPath = TempDb("two-transfers");
        try
        {
            string heroId, bobId, carolId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Race-Seller");
                var (_, bob) = await first.RegisterAsync("Race-Middleman");
                var (_, carol) = await first.RegisterAsync("Race-Final-Owner");
                (bobId, carolId) = (bob.PlayerId, carol.PlayerId);
                heroId = (await alice.ClaimStartersAsync())[0].Id;   // mint's inline save made it durable
            }

            // ── the race, over the same database the host just wrote ──
            var gate = new HoldSaveOpenGate();
            var flushSide = new SqliteGameStatePersistence(FactoryOn(dbPath, gate));
            var inlineSide = new SqliteGameStatePersistence(FactoryOn(dbPath));   // requests' persistence — never held

            var store = new GameStore();
            await inlineSide.LoadIntoAsync(store);   // the restart-shaped boot: live aggregates off the rows
            var hero = store.Heroes[heroId];

            // Recent grinding: the hero is legitimately in the dirty set when the sale happens.
            (hero.Level, hero.Xp) = (4, 250);
            store.MarkHeroDirty(heroId);

            // Transfer #1 begins exactly as GameService does it: the live aggregate mutates FIRST
            // (ConfirmTransferAsync / ClaimPurchasedHeroAsync), the inline save follows. The flush pass
            // below starts inside that gap, so its snapshot copies owner = bob over a row still = alice.
            hero.OwnerId = bobId;

            var flush = new HeroFlushService(
                store, flushSide, Options.Create(new GameOptions()), NullLogger<HeroFlushService>.Instance);
            gate.Arm();
            var flushTask = Task.Run(() => flush.FlushDirtyHeroesAsync());
            await gate.Paused.WaitAsync(TimeSpan.FromSeconds(30));   // held just before its commit

            await inlineSide.SaveHeroAsync(hero);    // transfer #1's inline save: durable owner = bob

            hero.OwnerId = carolId;                  // transfer #2: bob sends it onward…
            await inlineSide.SaveHeroAsync(hero);    // …and THAT identity save is the last legitimate word

            gate.Release();
            await flushTask;                         // the flush's held snapshot commits after everything

            // The durable row must still say carol — a progression flush has no business moving ownership.
            await using (var db = await FactoryOn(dbPath).CreateDbContextAsync())
                Assert.Equal(carolId, (await db.Heroes.FindAsync(heroId))!.OwnerId);

            // And the player-facing consequence: a restarted server must rehydrate carol's hero as carol's.
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var rehydrated = restarted.Services.GetRequiredService<GameStore>().Heroes[heroId];
            Assert.Equal(carolId, rehydrated.OwnerId);
            Assert.Equal(4, rehydrated.Level);   // the flush's actual job — the grind — still survived
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>
    /// The SINGLE-transfer overlap, pinned safe: the flush snapshots BEFORE the transfer mutates anything,
    /// so its copied owner equals the row it loaded — and EF writes only columns whose value actually
    /// changed, so the held flush commit carries no owner at all and cannot revert the transfer that
    /// landed inside its window. This is why one identity change overlapping one flush was never enough
    /// to mis-own a hero; the two-change window in the test above was. Pinned so a rewrite of the save
    /// (or of the flush's write-set) that starts writing unchanged identity columns fails loudly here.
    /// </summary>
    [Fact]
    public async Task FlushCommittingAcrossOneTransfer_DoesNotRevertIt()
    {
        var dbPath = TempDb("one-transfer");
        try
        {
            string heroId, bobId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Overlap-Seller");
                var (_, bob) = await first.RegisterAsync("Overlap-Buyer");
                bobId = bob.PlayerId;
                heroId = (await alice.ClaimStartersAsync())[0].Id;
            }

            var gate = new HoldSaveOpenGate();
            var flushSide = new SqliteGameStatePersistence(FactoryOn(dbPath, gate));
            var inlineSide = new SqliteGameStatePersistence(FactoryOn(dbPath));

            var store = new GameStore();
            await inlineSide.LoadIntoAsync(store);
            var hero = store.Heroes[heroId];

            (hero.Level, hero.Xp) = (4, 250);
            store.MarkHeroDirty(heroId);

            var flush = new HeroFlushService(
                store, flushSide, Options.Create(new GameOptions()), NullLogger<HeroFlushService>.Instance);
            gate.Arm();
            var flushTask = Task.Run(() => flush.FlushDirtyHeroesAsync());
            await gate.Paused.WaitAsync(TimeSpan.FromSeconds(30));   // snapshot copied: owner = alice = the row

            hero.OwnerId = bobId;                    // the whole transfer lands inside the flush's window
            await inlineSide.SaveHeroAsync(hero);    // durable owner = bob

            gate.Release();
            await flushTask;

            await using var db = await FactoryOn(dbPath).CreateDbContextAsync();
            var row = await db.Heroes.FindAsync(heroId);
            Assert.Equal(bobId, row!.OwnerId);   // the transfer held
            Assert.Equal(4, row.Level);          // and the flush's progression write still landed
        }
        finally { CleanupDb(dbPath); }
    }
}
