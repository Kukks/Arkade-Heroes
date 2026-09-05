using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A gauntlet is billed, then paid, then run. The session lived only in memory, so a restart inside that
/// window took the run with it — and unlike a rename there is no reuse branch to soften the loss: opening
/// another gauntlet simply bills another fee. It is also the game's main PvE loop, so it is the
/// highest-frequency place a paid-for thing could vanish.
///
/// <para>Asserted on the REHYDRATED STORE rather than by re-running over HTTP, as <c>StateDurabilityTests</c>
/// does: <c>InMemoryChainService</c> is per-host, so a payment it witnessed is forgotten across a restart.
/// A real chain does not forget, which is exactly why only the game-side row needed making durable.</para>
/// </summary>
public class GauntletDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static void Cleanup(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task AGauntletBilledButNotYetRun_SurvivesARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-gauntlet-{Guid.NewGuid():N}.db");
        try
        {
            string gauntletId, invoiceId, heroId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("G-Payer");
                heroId = (await alice.ClaimStartersAsync())[0].Id;
                var open = await alice.Gauntlet.OpenAsync(heroId);
                gauntletId = open.GauntletId;
                invoiceId = open.FeeInvoice.InvoiceId;
                await alice.PayInvoiceAsync(invoiceId);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to boot so the rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Gauntlets.TryGetValue(gauntletId, out var session),
                "a gauntlet the player may already have paid for must survive a restart — the fee is gone otherwise");
            Assert.Equal(heroId, session!.HeroId);
            // The invoice is what proves the fee was paid, and the SEED is what makes the run verifiable —
            // a rehydrated session missing either would be unrunnable in a different way.
            Assert.Equal(invoiceId, session.FeeInvoiceId);
            Assert.Equal(session.CommitmentHex, ArkadeHeroes.Core.Fairness.CommitReveal.Commit(session.ServerSeed));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AGauntletAlreadyRunIsNotRehydrated()
    {
        // One fee buys one run. A finished session coming back would hand a stale client a second.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-gauntlet-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("G-Done");
                var heroId = (await alice.ClaimStartersAsync())[0].Id;
                var open = await alice.Gauntlet.OpenAsync(heroId);
                await alice.PayInvoiceAsync(open.FeeInvoice.InvoiceId);
                await alice.Gauntlet.RunAsync(open.GauntletId, "durability-nonce");
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().Gauntlets);
        }
        finally { Cleanup(dbPath); }
    }
}
