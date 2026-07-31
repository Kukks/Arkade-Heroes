using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <summary>A host on a given state DB. The daily faucet ships closed, and opt-in rather than blanket
    /// so the 20-odd hosts here that never touch it keep testing the shipped configuration.</summary>
    private static WebApplicationFactory<Program> HostOn(string dbPath, bool dailyFaucetOpen = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            if (dailyFaucetOpen) b.UseSetting("Game:DailyRewardEnabled", "true");
        });

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
            using (var first = HostOn(dbPath, dailyFaucetOpen: true))
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
    public async Task TreasuryFlowTotals_SurviveARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                _ = first.CreateClient();   // start the host so the schema is created
                var store = first.Services.GetRequiredService<GameStore>();
                await store.RecordInflowAsync("inv-item-a", "item", 500);
                await store.RecordInflowAsync("inv-item-b", "item", 250);   // same tag — the totals GROUP them
                await store.RecordInflowAsync("inv-breed-a", "breed", 120);
                await store.RecordOutflowAsync("daily", 900);
                await store.RecordOutflowAsync("wager", 2000);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var reloaded = restarted.Services.GetRequiredService<GameStore>();

            // Totals are never stored — they are grouped back out of the rows, so they cannot drift from
            // the movements they summarise.
            Assert.Equal(750, reloaded.TreasuryInflowByTag["item"]);
            Assert.Equal(120, reloaded.TreasuryInflowByTag["breed"]);
            Assert.Equal(900, reloaded.TreasuryOutflowByTag["daily"]);
            Assert.Equal(2000, reloaded.TreasuryOutflowByTag["wager"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AFeeCapturedByARealPurchase_SurvivesARestart()
    {
        // The tests around this one drive the tally directly. This one drives the MONEY PATH — invoice, pay,
        // claim — so the wiring from a real fee capture through to a durable row is exercised, not assumed.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        var price = ArkadeHeroes.Core.Equipment.ItemCatalog.Find("rusty-blade")!.PriceSats;
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Durable-Fee-Payer");
                await alice.BuyItemAsync("rusty-blade");
                Assert.Equal(price, first.Services.GetRequiredService<GameStore>().TreasuryInflowByTag["item"]);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Equal(price,
                restarted.Services.GetRequiredService<GameStore>().TreasuryInflowByTag.GetValueOrDefault("item"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AnInflowTalliedBeforeARestart_IsNotTalliedAgainAfterIt()
    {
        // THE reason the rows are persisted instead of the totals. Item purchases are durable and
        // re-delivery after a crash is deliberate, so the SAME invoice legitimately reaches the tally again
        // on the far side of a restart. If the totals survived but the already-counted set did not, that
        // second call would book the fee twice — and a treasury that over-reports its income reads as
        // solvent when it is not. Double-counted INCOME is the one direction there is no recovering from.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                _ = first.CreateClient();
                await first.Services.GetRequiredService<GameStore>()
                    .RecordInflowAsync("inv-item-redelivered", "item", 500);
            }

            using (var restarted = HostOn(dbPath))
            {
                _ = restarted.CreateClient();
                var reloaded = restarted.Services.GetRequiredService<GameStore>();
                Assert.Equal(500, reloaded.TreasuryInflowByTag["item"]);

                // The re-delivery: the same invoice, recorded a second time on a fresh process.
                await reloaded.RecordInflowAsync("inv-item-redelivered", "item", 500);
                Assert.Equal(500, reloaded.TreasuryInflowByTag["item"]);

                // Not simply frozen — a DIFFERENT invoice on the same tag still counts.
                await reloaded.RecordInflowAsync("inv-item-fresh", "item", 500);
                Assert.Equal(1000, reloaded.TreasuryInflowByTag["item"]);
            }

            // And the refusal is durable, not just an in-memory nicety: the re-record left no second row,
            // so a further restart still reads two fees, not three.
            using var again = HostOn(dbPath);
            _ = again.CreateClient();
            Assert.Equal(1000, again.Services.GetRequiredService<GameStore>().TreasuryInflowByTag["item"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Outflow_AppendsInsteadOfDeduping()
    {
        // Outflow has no natural key and has never been deduped: two identical payouts are two real
        // payouts. Giving the durable rows dedup semantics would silently swallow the second one and
        // under-report what the treasury actually paid — so they append under a surrogate key instead.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                _ = first.CreateClient();
                var store = first.Services.GetRequiredService<GameStore>();
                await store.RecordOutflowAsync("daily", 100);
                await store.RecordOutflowAsync("daily", 100);   // same tag, same amount, a second real payout
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var reloaded = restarted.Services.GetRequiredService<GameStore>();
            Assert.Equal(200, reloaded.TreasuryOutflowByTag["daily"]);

            await reloaded.RecordOutflowAsync("daily", 100);
            Assert.Equal(300, reloaded.TreasuryOutflowByTag["daily"]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ARestingHeroOffer_AndTheHeroItEscrows_SurviveARestart()
    {
        // The sharpest version of the strand. The hero asset is sitting in the offer covenant on-chain and the
        // hero RECORD is durable (so the game still believes the seller owns it), but the row linking seller to
        // offer id used to live only in memory. Losing it left the asset recoverable in principle — the
        // covenant params are durable, so the reclaim leaf can still be rebuilt — and undiscoverable in
        // practice: the market no longer listed it and the seller was never told the offer existed.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string offerId, heroId, sellerId, offerAddress;
            long fee;
            using (var first = HostOn(dbPath))
            {
                var (seller, sellerPlayer) = await first.RegisterAsync("Durable-Hero-Seller");
                sellerId = sellerPlayer.PlayerId;
                heroId = (await seller.ClaimStartersAsync())[0].Id;

                var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroId, 20_000));
                offerId = offer.OfferId;
                offerAddress = offer.OfferAddress;
                fee = offer.ListingFeeSats;

                // Deposit the hero into the covenant, then reconcile so the listing is durably `active` —
                // this is the state where real value is escrowed.
                await seller.Dev.FundOfferAsync(new { OfferId = offerId });
                await seller.Offers.GetAsync(offerId);
                Assert.Equal("active", first.Services.GetRequiredService<GameStore>().Offers[offerId].Status);
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Offers.ContainsKey(offerId),
                "an offer holding an escrowed asset must survive a restart — without the row nothing can name it to reclaim it");
            var recovered = store.Offers[offerId];
            Assert.Equal("hero", recovered.Kind);
            Assert.Equal(heroId, recovered.HeroId);
            Assert.Equal(sellerId, recovered.SellerId);
            Assert.Equal("active", recovered.Status);
            Assert.Equal(20_000, recovered.AskSats);
            // The covenant's address and its reclaim timelock come back too: between them the seller can be
            // shown WHERE the asset is and WHEN it unlocks.
            Assert.Equal(offerAddress, recovered.OfferAddress);
            Assert.True(recovered.RefundAfterUnixSeconds > 0);
            // The fee the covenant will route to the treasury on a sale — losing it would under-book the sale.
            Assert.Equal(fee, recovered.ListingFeeSats);

            // And the pair is CONSISTENT again: the hero the game still credits to the seller is the same hero
            // the surviving offer says is escrowed.
            Assert.Equal(sellerId, store.Heroes[heroId].OwnerId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AnOfferAwaitingItsDeposit_SurvivesARestart()
    {
        // Persisting on CREATE, not on first deposit, is what makes this safe: the seller already holds the
        // offer address, so they can deposit at any moment — including after a bounce. A row written only once
        // the asset was seen would leave exactly that deposit with nothing to name it.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string offerId;
            using (var first = HostOn(dbPath))
            {
                var (seller, _) = await first.RegisterAsync("Durable-Item-Seller");
                await seller.BuyItemAsync("rusty-blade");
                // Listed and NOT deposited — still `pending`.
                offerId = (await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000))).OfferId;
                Assert.Equal("pending", first.Services.GetRequiredService<GameStore>().Offers[offerId].Status);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Offers.ContainsKey(offerId),
                "a listing the seller can still deposit into must survive a restart — otherwise that deposit strands");
            Assert.Equal("pending", store.Offers[offerId].Status);
            Assert.Equal("rusty-blade", store.Offers[offerId].ItemId);
            // Rehydrated as NOT deposited — the flag is never stored, it is re-derived from the chain, and
            // false is the conservative starting point for the free-to-sell check that reads it.
            Assert.False(store.Offers[offerId].AssetDeposited);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ASoldOffer_IsNotRehydrated_ThoughItsRowSurvivesAsAnAuditMarker()
    {
        // `closed` is TERMINAL, and terminal rows stay out of the live store — exactly as resolved brackets
        // do. Rehydrating one would re-list a sale that already happened and hand the reconcile a second
        // chance to book its fee. The row itself is kept, so this asserts the LOAD FILTER and not merely the
        // absence of a write.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-{Guid.NewGuid():N}.db");
        try
        {
            string offerId;
            using (var first = HostOn(dbPath))
            {
                var (seller, _) = await first.RegisterAsync("Durable-Sold-Seller");
                var (buyer, _) = await first.RegisterAsync("Durable-Sold-Buyer");
                await seller.BuyItemAsync("rusty-blade");
                offerId = (await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000))).OfferId;
                await seller.Dev.FundOfferAsync(new { OfferId = offerId });
                await buyer.Offers.ListAsync();                                 // reconcile → active
                await buyer.Dev.FulfillOfferAsync(new { OfferId = offerId });    // the sale
                await buyer.Offers.ListAsync();                                 // reconcile → closed
                Assert.Equal("closed", first.Services.GetRequiredService<GameStore>().Offers[offerId].Status);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();

            Assert.False(restarted.Services.GetRequiredService<GameStore>().Offers.ContainsKey(offerId),
                "a sold offer must not come back — it would re-list an item that has already changed hands");

            // The durable row IS there, closed: the close was recorded, it is simply not rehydrated.
            var dbFactory = restarted.Services.GetRequiredService<
                Microsoft.EntityFrameworkCore.IDbContextFactory<
                    ArkadeHeroes.Server.Persistence.GameStateDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.Offers.FindAsync(offerId);
            Assert.NotNull(row);
            Assert.Equal("closed", row!.Status);
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

    /// <summary>
    /// A server running without durability must SAY so, at boot, at warning.
    ///
    /// <para>The opt-in default is right for <c>dotnet run</c> and catastrophic in production, and the two
    /// are indistinguishable from the outside: the server boots, serves, and passes its healthcheck while
    /// writing nothing, so the first anyone learns of it is a restart taking the whole roster. The heroes
    /// themselves are on-chain and survive — it is the only record of whose they are that does not, which
    /// makes them permanently invisible rather than merely misplaced.</para>
    ///
    /// <para>So the absence is loud, and — the other half, or the warning becomes noise nobody reads — a
    /// server that IS configured says nothing of the kind.</para>
    /// </summary>
    [Fact]
    public async Task WithNoStatePathConfigured_TheServerWarnsAtBoot_AndWithOneItDoesNot()
    {
        var silent = new CapturingLoggerProvider();
        using (var volatileHost = new WebApplicationFactory<Program>().WithWebHostBuilder(
            b => b.ConfigureLogging(l => l.AddProvider(silent).SetMinimumLevel(LogLevel.Warning))))
        {
            await volatileHost.RegisterAsync("Warned-Player");   // forces the host to actually boot
        }

        var warning = Assert.Single(silent.Warnings, w => w.Contains("State durability DISABLED"));
        // Names the key that fixes it, and what is at stake — a warning that only says "disabled" leaves
        // the operator to guess both.
        Assert.Contains("Game__StateDbPath", warning);
        Assert.Contains("restart destroys them", warning);

        var quiet = new CapturingLoggerProvider();
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-durability-warn-{Guid.NewGuid():N}.db");
        try
        {
            using (var durable = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureLogging(l => l.AddProvider(quiet).SetMinimumLevel(LogLevel.Warning));
            }))
            {
                await durable.RegisterAsync("Durable-Player");
            }
            Assert.DoesNotContain(quiet.Warnings, w => w.Contains("State durability DISABLED"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The shipped IMAGE turns durability on by itself.
    ///
    /// <para>Every test above hands the server a state path, so none of them can notice a DEPLOYMENT that
    /// forgets one — and that is the failure that actually happened: the compose file sets the key, but a
    /// platform that deploys the published image directly never reads that file, and the app's own default
    /// is no persistence at all. Defaulting it in the Dockerfile is what makes "deployed" mean "durable"
    /// however the image is launched; this is the test that keeps it there.</para>
    ///
    /// <para>The path must sit under the <c>/data</c> mount point, since a database written anywhere else
    /// in the image lives in the container's writable layer and dies with the container.</para>
    /// </summary>
    [Fact]
    public void TheShippedImage_DefaultsTheStateDatabaseOntoItsVolume()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "src", "ArkadeHeroes.Server", "Dockerfile"));

        var env = System.Text.RegularExpressions.Regex.Match(
            dockerfile, @"^ENV\s+Game__StateDbPath=(?<path>\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(env.Success,
            "the server image must default Game__StateDbPath — without it a deployment that does not use "
            + "docker-compose.yml persists nothing, and says nothing about it either.");
        Assert.StartsWith("/data/", env.Groups["path"].Value);
        Assert.Contains("VOLUME [\"/data\"]", dockerfile);

        // And compose still mounts a NAMED volume there. The image's own VOLUME is anonymous: it survives a
        // restart but is orphaned when the container is recreated, which is exactly what a redeploy does.
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        Assert.Contains("- arkade-state:/data", compose);
        Assert.Contains("Game__StateDbPath: /data/", compose);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }

    /// <summary>Collects warning-level log messages so a test can assert on what an operator would see.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }
        public ILogger CreateLogger(string categoryName) => new Sink(_warnings);
        public void Dispose() { }

        private sealed class Sink(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (warnings) warnings.Add(formatter(state, exception));
            }
        }
    }
}
