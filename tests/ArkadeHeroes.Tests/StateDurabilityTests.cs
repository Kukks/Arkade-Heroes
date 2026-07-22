using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Durability of the state a restart must not lose. A player pays real sats for an item and claims it in a
/// second call — if the server bounces in between and the purchase only ever lived in a dictionary, the
/// treasury keeps the sats and the player gets nothing, with no recovery path. These tests drive a REAL
/// restart: a second host, a fresh GameStore, the same database file.
///
/// Persistence is opt-in (<c>Game:StateDbPath</c>); with no path the server keeps its historical in-memory
/// behaviour, which is what every other test in this suite exercises.
/// </summary>
public class StateDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    [Fact]
    public async Task PaidButUnclaimedPurchase_SurvivesARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string invoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Durable-Buyer");
                var bought = await alice.Items.BuyAsync("rusty-blade");   // creates the purchase, unclaimed
                invoiceId = bought.Invoice.InvoiceId;
                Assert.True(first.Services.GetRequiredService<GameStore>().ItemPurchases.ContainsKey(invoiceId));
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.ItemPurchases.ContainsKey(invoiceId),
                "a purchase the player may already have paid for must survive a restart — otherwise the sats are gone");
            var recovered = store.ItemPurchases[invoiceId];
            Assert.Equal("rusty-blade", recovered.ItemId);
            Assert.Equal("pending", recovered.Status);   // still claimable, which is the whole point
        }
        finally
        {
            // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
            // file is harmless either way — never fail a durability test on its own housekeeping.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task OpenTournamentWithPaidBuyIns_SurvivesARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string tournamentId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Durable-Opener");
                var hero = (await alice.ClaimStartersAsync())[0];
                // A 4-hero bracket stays OPEN with one entrant — a buy-in invoice exists and may be paid.
                var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(hero.Id, 1000, 4));
                tournamentId = open.Tournament.Id;
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Tournaments.ContainsKey(tournamentId),
                "an unresolved bracket holding paid buy-ins must survive a restart — the pot is real sats");
            var recovered = store.Tournaments[tournamentId];
            Assert.Single(recovered.Entrants);                 // the opener, with their buy-in invoice id
            Assert.Equal(4, recovered.Size);
            Assert.Equal(1000, recovered.BuyInSats);
            Assert.NotEqual("resolved", recovered.Status);
            Assert.False(string.IsNullOrEmpty(recovered.Entrants[0].BuyInInvoiceId));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task PlayerIdentity_AndItsOnceOnlyFlags_SurviveARestart()
    {
        // Identity is the anchor: a surviving purchase or bracket names a PlayerId, so without the player
        // it's a dangling record nobody can claim. And StarterClaimed / LastClaimDay are once-only flags —
        // losing them would let the same player re-mint free starters and re-claim today's faucet reward.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string playerId;
            using (var first = HostOn(dbPath))
            {
                var chain = (ArkadeHeroes.Chain.InMemoryChainService)
                    first.Services.GetRequiredService<ArkadeHeroes.Chain.IChainService>();
                chain.FundTreasury(50_000);

                var (alice, player) = await first.RegisterAsync("Durable-Identity");
                playerId = player.PlayerId;
                await alice.ClaimStartersAsync();      // consumes the once-only starter grant
                await alice.Daily.ClaimAsync();        // consumes today's faucet claim
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Players.ContainsKey(playerId), "identity must survive — everything else references it");
            var recovered = store.Players[playerId];
            Assert.True(recovered.StarterClaimed, "a restart must not hand out a second set of free starters");
            Assert.NotNull(recovered.LastClaimDay);   // today stays consumed — no double faucet payout
            Assert.Equal(1, recovered.StreakCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FancyDiscoveryEditionsAndCount_SurviveARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                _ = first.CreateClient();   // start the host so the schema is created
                var persistence = first.Services
                    .GetRequiredService<ArkadeHeroes.Server.Persistence.IGameStatePersistence>();
                // Three Emberlords, found in order: player-1 discovered the set, then #2 and #3 turned up.
                await persistence.SaveFancyFindAsync(new FancyFind("Emberlord", "hero-1", "Alpha", "player-1", 100, 1));
                await persistence.SaveFancyFindAsync(new FancyFind("Emberlord", "hero-2", "Beta", "player-2", 200, 2));
                await persistence.SaveFancyFindAsync(new FancyFind("Emberlord", "hero-3", "Gamma", "player-3", 300, 3));
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            // "First to breed this set, forever" — the discoverer survives the restart.
            Assert.Equal("player-1", store.FancyDiscoveries["Emberlord"].OwnerId);
            Assert.Equal(3, store.FancyEditionByHero["hero-3"].Edition);
            Assert.Equal(3, store.FancyFindCount["Emberlord"]);
            // The load-bearing property: the next LIVE find is #4, not a second "#1" from a reset count.
            Assert.Equal(4, store.RecordFancyFind("Emberlord", "hero-4", "Delta", "player-4", 400)!.Edition);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task WithNoStatePathConfigured_NothingIsPersisted()
    {
        // The default: no database, no file, historical behaviour. Guards against persistence silently
        // switching itself on (and writing a stray db) for every existing test and deployment.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Volatile-Buyer");
        var bought = await alice.Items.BuyAsync("rusty-blade");

        var persistence = factory.Services.GetRequiredService<ArkadeHeroes.Server.Persistence.IGameStatePersistence>();
        Assert.IsType<ArkadeHeroes.Server.Persistence.NullGameStatePersistence>(persistence);
        Assert.True(factory.Services.GetRequiredService<GameStore>().ItemPurchases.ContainsKey(bought.Invoice.InvoiceId));
    }
}
