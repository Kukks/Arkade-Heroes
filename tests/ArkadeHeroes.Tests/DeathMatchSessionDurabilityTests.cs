using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A death-match parks BOTH heroes and their equipped gear in one joint escrow, and one of those heroes is
/// going to be burned. <c>ListReclaimableAsync</c> finds that escrow by walking <c>store.DeathMatches</c> —
/// a dictionary nothing persisted, so a restart left two players unable to name what held their heroes.
/// </summary>
public class DeathMatchSessionDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static void Cleanup(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task AnOpenDeathMatchEscrow_IsStillReclaimableAfterARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-dm-{Guid.NewGuid():N}.db");
        try
        {
            string deathMatchId, escrowAddress, challengerFeeInvoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("DM-Challenger");
                var (bob, _) = await first.RegisterAsync("DM-Defender");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.DeathMatch.OpenAsync(
                    new DeathMatchOpenRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
                deathMatchId = open.DeathMatchId;
                escrowAddress = open.EscrowAddress;
                challengerFeeInvoiceId = open.FeeInvoice!.InvoiceId;
                await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = deathMatchId, Role = "challenger" });
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.DeathMatches.TryGetValue(deathMatchId, out var session),
                "without this row /reclaim cannot name the escrow holding the challenger's staked hero");
            Assert.Equal(escrowAddress, session!.JointEscrowAddress);
            Assert.Equal(challengerFeeInvoiceId, session.ChallengerFeeInvoiceId);
            Assert.False(session.Accepted);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AnAcceptedDeathMatch_ComesBackAcceptedWithTheDefendersFee()
    {
        // Acceptance IS the defender staking, and the only thing that moves after open — a row frozen at
        // the opening state would come back describing a match nobody had staked into.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-dm-{Guid.NewGuid():N}.db");
        try
        {
            string deathMatchId, defenderFeeInvoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("DM-Challenger2");
                var (bob, _) = await first.RegisterAsync("DM-Defender2");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.DeathMatch.OpenAsync(
                    new DeathMatchOpenRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
                deathMatchId = open.DeathMatchId;
                await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = deathMatchId, Role = "challenger" });
                var accept = await bob.DeathMatch.AcceptAsync(deathMatchId);
                defenderFeeInvoiceId = accept.FeeInvoice!.InvoiceId;
                await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = deathMatchId, Role = "defender" });
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.DeathMatches.TryGetValue(deathMatchId, out var session));
            Assert.True(session!.Accepted, "both heroes are staked, so the rehydrated match must say so");
            Assert.Equal(defenderFeeInvoiceId, session.DefenderFeeInvoiceId);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task ASettledDeathMatch_IsNotRehydrated()
    {
        // The escrow is spent and the loser burned; a returning row would offer a reclaim against nothing.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-dm-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("DM-Challenger3");
                var (bob, _) = await first.RegisterAsync("DM-Defender3");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.DeathMatch.OpenAsync(
                    new DeathMatchOpenRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
                await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
                await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
                var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
                await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
                await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
                await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("dm-durable"));
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().DeathMatches);
        }
        finally { Cleanup(dbPath); }
    }
}
