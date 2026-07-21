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
