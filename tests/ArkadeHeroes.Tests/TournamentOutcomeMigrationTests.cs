using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The outcome columns land on a database that ALREADY EXISTS.
///
/// <para>A migration that only ever runs against a fresh file is untested where it matters: production's
/// <c>/data</c> holds brackets written by the previous schema, and the server applies migrations on boot
/// (<c>Database.MigrateAsync</c>) before it serves anything. If this one could not evolve that file, the
/// process would not start at all.</para>
///
/// <para>The second half matters as much and is easy to miss: rows already on disk when the columns arrive
/// have NO outcome and never will — nothing recorded one at the time, and it cannot be recovered now. Those
/// brackets rehydrate as resolved-with-no-champion, which is a state the client is required to survive
/// honestly rather than a state to paper over with a re-derived guess.</para>
/// </summary>
public class TournamentOutcomeMigrationTests
{
    /// <summary>The migration immediately BEFORE the outcome columns — the schema production is on.</summary>
    private const string PreviousMigration = "20260801064827_RecordFailedPayouts";

    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"arkade-tourney-migration-{tag}-{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    private static GameStateDbContext ContextOn(string dbPath) =>
        new(new DbContextOptionsBuilder<GameStateDbContext>().UseSqlite($"Data Source={dbPath}").Options);

    /// <summary>
    /// Brings <paramref name="dbPath"/> up to the PREVIOUS schema and writes one resolved bracket into it —
    /// the row shape the old binary produced, before the outcome columns existed.
    ///
    /// <para>Raw SQL rather than EF: the entity type in this build already carries the new columns, so an
    /// EF insert here would describe a table that does not exist yet. The entrants JSON goes in as a
    /// PARAMETER because <c>ExecuteSqlRaw</c> reads braces in the literal as format placeholders.</para>
    /// </summary>
    private static async Task SeedLegacyResolvedBracketAsync(string dbPath)
    {
        await using var old = ContextOn(dbPath);
        await old.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await old.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Tournaments"
                ("Id","OpenerPlayerId","BuyInSats","Size","ServerSeed","CommitmentHex","Status","EntrantsJson")
            VALUES
                ('tourney-legacy','player-1',1000,4,X'00112233','abcd','resolved',{0})
            """,
            """[{"PlayerId":"player-1","HeroId":"hero-1","BuyInInvoiceId":"inv-1"}]""");
    }

    [Fact]
    public async Task TheOutcomeColumns_LandOnADatabaseThatAlreadyHoldsBrackets()
    {
        var dbPath = TempDb("upgrade");
        try
        {
            // ── a database at the PREVIOUS schema, with a resolved bracket already in it ──
            await SeedLegacyResolvedBracketAsync(dbPath);

            // ── the boot-time upgrade the server performs ──
            await using (var upgraded = ContextOn(dbPath))
            {
                await upgraded.Database.MigrateAsync();

                // The pre-existing row survived the upgrade intact…
                var row = await upgraded.Tournaments.AsNoTracking().SingleAsync(t => t.Id == "tourney-legacy");
                Assert.Equal("resolved", row.Status);
                Assert.Equal(1000, row.BuyInSats);
                Assert.Equal("abcd", row.CommitmentHex);

                // …and the new columns are there, empty, because nothing ever recorded an outcome for it.
                Assert.Null(row.ResultJson);
                Assert.Null(row.PrizesJson);
                Assert.Null(row.EntrantSnapshotsJson);
                Assert.Null(row.Nonce);
                Assert.Null(row.EntropyHex);
                Assert.Null(row.EntrantsCommitmentHex);
                Assert.Null(row.ConfigVersion);
                Assert.Null(row.ContentVersion);

                // And the upgraded schema takes a write on those columns, which is the point of adding them.
                row = await upgraded.Tournaments.SingleAsync(t => t.Id == "tourney-legacy");
                row.ResultJson = """{"ChampionId":"hero-1","Matches":[],"Rounds":0}""";
                await upgraded.SaveChangesAsync();
            }
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>
    /// A bracket that resolved under the OLD binary rehydrates as what it is: resolved, with no champion.
    /// That absence is permanent and correct — the champion was never written down, and the one thing that
    /// must not happen is the server inventing a replacement by re-running the bracket under today's rules.
    /// It reports the honest gap and refuses to settle again, which is the state <c>#229</c> pinned the
    /// client against.
    /// </summary>
    [Fact]
    public async Task ABracketResolvedBeforeTheColumnsExisted_ComesBackAsResolvedWithNoChampion()
    {
        var dbPath = TempDb("legacy");
        var chain = new InMemoryChainService();
        chain.FundTreasury(100_000);
        try
        {
            await SeedLegacyResolvedBracketAsync(dbPath);

            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            });
            var anon = new ArkadeHeroesClient(host.CreateClient());

            // The server booted over the old file — which is itself the migration assertion — and the
            // bracket is readable, saying only what it can actually prove.
            var dto = await anon.Tournament.GetAsync("tourney-legacy");
            Assert.Equal("resolved", dto.Status);
            Assert.Null(dto.ChampionHeroId);
            Assert.Equal(0, dto.ChampionPrizeSats);

            // No replay to give: there is no stored bracket and no fill-time snapshot to re-run one from.
            // A 404 is the honest answer; a re-derived bracket would be a fabricated one.
            var noReplay = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
                () => anon.Tournament.ReplayAsync("tourney-legacy"));
            Assert.Contains("404", noReplay.Message);

            // And it is still terminal: the missing outcome is not an opening to settle it a second time.
            var svc = host.Services.GetRequiredService<GameService>();
            var settleAgain = await Assert.ThrowsAsync<GameRuleException>(
                () => svc.RefundTournamentAsync("tourney-legacy", null, CancellationToken.None));
            Assert.Contains("already resolved", settleAgain.Message);
        }
        finally { CleanupDb(dbPath); }
    }
}
