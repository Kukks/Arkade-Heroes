using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A covenant breed or fusion parks both heroes and the fee in an escrow, and <c>/reclaim</c> finds them by
/// walking the in-memory session dictionary. Neither dictionary was persisted, so a restart did not merely
/// lose a fee — it made the escrow UNNAMEABLE, and a player could not reclaim a thing they could no longer
/// point at. Recoverable in principle and unfindable in practice is indistinguishable from lost.
/// </summary>
public class EscrowSessionDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static void Cleanup(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task ACovenantBreedEscrow_IsStillReclaimableAfterARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-escrow-{Guid.NewGuid():N}.db");
        try
        {
            string breedingId, escrowAddress;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("E-Breeder");
                var heroes = await alice.ClaimStartersAsync();
                var commit = await alice.Breeding.CommitAsync(
                    new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));
                breedingId = commit.BreedingId;
                escrowAddress = commit.EscrowAddress!;
                Assert.NotNull(escrowAddress);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Breedings.TryGetValue(breedingId, out var session),
                "without this row /reclaim cannot name the escrow holding both parents");
            // The ADDRESS is the part that matters: it is assigned after the session is first built, so a
            // row that saved only the opening state would come back pointing at nothing.
            Assert.Equal(escrowAddress, session!.EscrowAddress);
            Assert.Equal("covenant", session.Mode);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task ACovenantMergeEscrow_IsStillReclaimableAfterARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-escrow-{Guid.NewGuid():N}.db");
        try
        {
            string mergeId, escrowAddress;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("E-Fuser");
                var heroes = await alice.ClaimStartersAsync();
                var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));
                mergeId = commit.MergeId;
                escrowAddress = commit.EscrowAddress!;
                Assert.NotNull(escrowAddress);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Merges.TryGetValue(mergeId, out var session),
                "without this row /reclaim cannot name the escrow holding base and sacrifice");
            Assert.Equal(escrowAddress, session!.EscrowAddress);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AResolvedFusionIsNotRehydrated()
    {
        // A spent escrow has nothing to reclaim, so it must not come back and offer one.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-escrow-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("E-Done");
                var heroes = await alice.ClaimStartersAsync();
                var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));
                await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
                await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("escrow-nonce"));
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().Merges);
        }
        finally { Cleanup(dbPath); }
    }
}
