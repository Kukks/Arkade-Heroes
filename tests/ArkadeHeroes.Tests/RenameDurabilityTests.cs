using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A rename is billed, then paid, then confirmed. The pending session lived only in memory, so a restart
/// inside that window cost the player twice: the rename they had paid for was gone, and the "don't bill
/// twice" branch in <c>RequestRenameAsync</c> — which reads that same dictionary — then found no prior
/// invoice and charged a second fee for a rename already bought.
///
/// <para>These assert on the REHYDRATED STORE rather than re-driving the HTTP flow, the way
/// <c>StateDurabilityTests</c> does and for the same reason: <c>InMemoryChainService</c> is per-host, so a
/// payment it witnessed is forgotten across a restart. A real chain is the durable layer and does not
/// forget — which is exactly why the game-side row is the only thing that had to be made durable.</para>
/// </summary>
public class RenameDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:HeroRenameFeeSats", "500");
        });

    private static void Cleanup(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task ARenameBilledButNotYetConfirmed_SurvivesARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-rename-{Guid.NewGuid():N}.db");
        try
        {
            string heroId, invoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("R-Payer");
                heroId = (await alice.ClaimStartersAsync())[0].Id;
                var quote = await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("Solstice Vanguard"));
                invoiceId = quote.Fee!.InvoiceId;
                await alice.PayInvoiceAsync(invoiceId);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to boot so the rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Renames.TryGetValue(heroId, out var pending),
                "a rename the player may already have paid for must survive a restart, or they are billed twice");
            Assert.Equal("Solstice Vanguard", pending!.NewName);
            // The INVOICE id is the load-bearing half: it is what the reuse branch matches on to decide a
            // rename is already paid for, and what confirm re-checks before applying the name.
            Assert.Equal(invoiceId, pending.FeeInvoiceId);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task RetargetingToADifferentName_KeepsTheOneInvoiceAcrossARestart()
    {
        // One paid fee buys one APPLIED rename however many names are tried, so a retarget must carry the
        // original invoice forward — otherwise the durable row forgets what was paid for.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-rename-{Guid.NewGuid():N}.db");
        try
        {
            string heroId, invoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("R-Retarget");
                heroId = (await alice.ClaimStartersAsync())[0].Id;
                var quote = await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("First Choice"));
                invoiceId = quote.Fee!.InvoiceId;
                await alice.PayInvoiceAsync(invoiceId);
                await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("Second Choice"));
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var pending = restarted.Services.GetRequiredService<GameStore>().Renames[heroId];

            Assert.Equal("Second Choice", pending.NewName);
            Assert.Equal(invoiceId, pending.FeeInvoiceId);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AnAppliedRenameIsNotRehydrated()
    {
        // The row is dropped when the rename lands, so one paid fee still buys exactly one APPLIED rename
        // and a restart cannot hand the player a second free one.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-rename-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("R-Done");
                var heroId = (await alice.ClaimStartersAsync())[0].Id;
                var quote = await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("Applied Already"));
                await alice.PayInvoiceAsync(quote.Fee!.InvoiceId);
                Assert.Equal("Applied Already", (await alice.Heroes.ConfirmRenameAsync(heroId)).Name);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().Renames);
        }
        finally { Cleanup(dbPath); }
    }
}
