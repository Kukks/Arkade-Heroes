using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The inline-vs-inline identity write race — the residual left open when the flush-vs-identity one was
/// closed. <see cref="HeroFlushIdentityRaceTests"/> stopped the periodic flush from carrying identity
/// columns at all, which settled every race BETWEEN a flush and an identity save. It could not settle two
/// IDENTITY saves racing each other: a transfer, a marketplace claim, a rename and an absorb all mutate the
/// live hero and then call the same read-modify-commit save, with awaits between every step. Two that
/// overlap can commit in the opposite order to the one they read in, and the loser's stale copy is the
/// durable word.
///
/// These stretch the real save's own load-snapshot-commit window deterministically (an EF interceptor holds
/// one SaveChanges open — the gap the scheduler and SQLite's write queue open nondeterministically under
/// load) and land a second identity save inside it. Production code on both sides: one real
/// SqliteGameStatePersistence, exactly as the server resolves a single one from DI, and the real boot-time
/// rehydrate for the player-facing consequence.
/// </summary>
public class HeroIdentitySaveRaceTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"arkade-identity-race-{tag}-{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    private static PooledDbContextFactory<GameStateDbContext> FactoryOn(string dbPath, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<GameStateDbContext>()
            .UseSqlite($"Data Source={dbPath}").AddInterceptors(interceptors).Options);

    /// <summary>Holds ONE armed SaveChanges open between the save's field-copy and its commit. Unarmed saves
    /// pass straight through, so the same persistence instance can serve both sides of the race.</summary>
    private sealed class HoldSaveOpenGate : SaveChangesInterceptor
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

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
    /// Two transfers of one hero, the second beginning while the first's save is still between its snapshot
    /// and its commit. The first save copied owner = bob and is held; the second reads the row (still the
    /// seller), copies owner = carol and commits. When the held save finally commits it writes its own stale
    /// bob over carol — the hero is durably owned by the middleman it has already left, while memory says
    /// carol. A restart then rehydrates a hero the server believes belongs to someone the chain shows no
    /// longer holds it: the mis-own the PersistedHero contract forbids, reached without the flush being
    /// involved at all.
    /// </summary>
    [Fact]
    public async Task TwoIdentitySavesRacing_MustNotRevertTheDurableOwner()
    {
        var dbPath = TempDb("two-identity-saves");
        try
        {
            string heroId, bobId, carolId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Identity-Seller");
                var (_, bob) = await first.RegisterAsync("Identity-Middleman");
                var (_, carol) = await first.RegisterAsync("Identity-Final-Owner");
                (bobId, carolId) = (bob.PlayerId, carol.PlayerId);
                heroId = (await alice.ClaimStartersAsync())[0].Id;
            }

            // ONE persistence, as the server has: both racing saves go through the same instance, and the
            // gate arms only the first of them.
            var gate = new HoldSaveOpenGate();
            var persistence = new SqliteGameStatePersistence(FactoryOn(dbPath, gate));

            var store = new GameStore();
            await persistence.LoadIntoAsync(store);
            var hero = store.Heroes[heroId];

            // Transfer #1, exactly as GameService orders it: mutate the live aggregate, then save.
            hero.OwnerId = bobId;
            gate.Arm();
            var heldSave = Task.Run(() => persistence.SaveHeroAsync(hero));
            await gate.Paused.WaitAsync(TimeSpan.FromSeconds(30));   // snapshot taken (owner = bob), commit held

            // Transfer #2 begins inside that window: this is the last legitimate word. It may or may not get
            // to run concurrently — serializing the two saves is a fine way to fix this, and then it simply
            // queues behind the held one. What must hold either way is the OUTCOME below.
            hero.OwnerId = carolId;
            var secondSave = persistence.SaveHeroAsync(hero);

            gate.Release();
            await heldSave.WaitAsync(TimeSpan.FromSeconds(30));
            await secondSave.WaitAsync(TimeSpan.FromSeconds(30));   // a timeout here would mean a deadlock

            await using (var db = await FactoryOn(dbPath).CreateDbContextAsync())
                Assert.Equal(carolId, (await db.Heroes.FindAsync(heroId))!.OwnerId);

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            Assert.Equal(carolId, restarted.Services.GetRequiredService<GameStore>().Heroes[heroId].OwnerId);
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>
    /// The same shape across two DIFFERENT identity fields — a rename racing a transfer. Serializing the
    /// saves has to keep BOTH writes, not just order them: whichever commits second must carry the other's
    /// change too, because each save copies the whole live aggregate. Pins that the fix serializes rather
    /// than merely dropping the losing write.
    /// </summary>
    [Fact]
    public async Task ARenameRacingATransfer_KeepsBothChanges()
    {
        var dbPath = TempDb("rename-vs-transfer");
        try
        {
            string heroId, bobId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Rename-Seller");
                var (_, bob) = await first.RegisterAsync("Rename-Buyer");
                bobId = bob.PlayerId;
                heroId = (await alice.ClaimStartersAsync())[0].Id;
            }

            var gate = new HoldSaveOpenGate();
            var persistence = new SqliteGameStatePersistence(FactoryOn(dbPath, gate));

            var store = new GameStore();
            await persistence.LoadIntoAsync(store);
            var hero = store.Heroes[heroId];

            hero.Name = "Renamed Champion";
            gate.Arm();
            var heldSave = Task.Run(() => persistence.SaveHeroAsync(hero));
            await gate.Paused.WaitAsync(TimeSpan.FromSeconds(30));

            hero.OwnerId = bobId;
            var secondSave = persistence.SaveHeroAsync(hero);

            gate.Release();
            await heldSave;
            await secondSave.WaitAsync(TimeSpan.FromSeconds(30));

            await using var db = await FactoryOn(dbPath).CreateDbContextAsync();
            var row = await db.Heroes.FindAsync(heroId);
            Assert.Equal(bobId, row!.OwnerId);                 // the transfer held
            Assert.Equal("Renamed Champion", row.Name);        // and so did the rename
        }
        finally { CleanupDb(dbPath); }
    }
}
