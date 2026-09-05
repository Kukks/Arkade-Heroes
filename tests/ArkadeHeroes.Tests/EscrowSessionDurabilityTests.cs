using ArkadeHeroes.Chain;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Covenant mode survives a re-reveal because the first one SPENDS the escrow. An invoice has
    /// no such spend and reads paid forever, so a rehydrated session sailed back through the gate and minted
    /// a second child — and real sats bought it, so one fee must buy exactly one.</summary>
    [Theory]
    [InlineData("crash-nonce")]
    [InlineData("a-different-nonce")]   // the retry's nonce is IGNORED, never allowed to remake the child
    public async Task ACrashBetweenAnInvoiceBreedsMintAndItsCompletion_NeverMintsASecondChild(string retryNonce)
    {
        // ONE chain across both hosts: it is the outside world, so the fee paid before the bounce still
        // reads paid after it — which is exactly why the invoice gate cannot refuse the second reveal.
        var chain = new InMemoryChainService();
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-escrow-{Guid.NewGuid():N}.db");
        try
        {
            string playerId, breedingId, childId;
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s =>
                {
                    s.AddSingleton<IChainService>(chain);
                    s.AddSingleton<IGameStatePersistence>(sp => new DiesOnceTheChildIsDurable(
                        new SqliteGameStatePersistence(sp.GetRequiredService<IDbContextFactory<GameStateDbContext>>())));
                });
            }))
            {
                var (alice, dto) = await first.RegisterAsync("Breed-Crash");
                playerId = dto.PlayerId;
                var heroes = await alice.ClaimStartersAsync();
                var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
                breedingId = commit.BreedingId;
                await alice.Dev.PayInvoiceAsync(new { InvoiceId = commit.Invoice!.InvoiceId });
                childId = (await alice.Breeding.RevealAsync(
                    breedingId, new BreedRevealRequest("crash-nonce"))).Hero.Id;
            }

            using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            });
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();
            var svc = restarted.Services.GetRequiredService<GameService>();

            Assert.True(store.Heroes.ContainsKey(childId), "the child's own row landed before the crash");
            Assert.True(store.Breedings.ContainsKey(breedingId),
                "the session must come back unfinished, or there is no second reveal to survive");

            var second = await svc.RevealBreedingAsync(
                store.Players[playerId], breedingId, retryNonce, CancellationToken.None);

            Assert.Equal(childId, second.Child.Id);
            Assert.Single(store.Heroes.Values, h => h.OwnerId == playerId && h.ParentAId is not null);

            // The reconciled hero must BE this session's child, not merely some hero: right owner, both
            // recorded parents, and the commit-reveal proof that lets a client verify it.
            Assert.Equal(playerId, second.Child.OwnerId);
            Assert.Equal(store.Breedings[breedingId].ParentAId, second.Child.ParentAId);
            Assert.Equal(store.Breedings[breedingId].ParentBId, second.Child.ParentBId);
            Assert.False(string.IsNullOrEmpty(second.Child.PlayerNonce));
            Assert.False(string.IsNullOrEmpty(second.Child.EntropyHex));
            // The proof handed back is the one that MADE this child, whatever nonce the retry arrived with.
            Assert.Equal(second.Child.EntropyHex, second.EntropyHex);
            Assert.Equal(second.Child.PlayerNonce, second.Receipt.Nonce);
        }
        finally { Cleanup(dbPath); }
    }

    /// <summary>Real persistence until the bred CHILD's row is on disk, then every later write is lost —
    /// the crash window this bug lives in. Only a bred hero carries parents, so the first such save is
    /// the child's.</summary>
    private sealed class DiesOnceTheChildIsDurable(IGameStatePersistence inner) : IGameStatePersistence
    {
        private volatile bool _dead;
        public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => inner.LoadIntoAsync(store, ct);
        public async Task SaveHeroAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default)
        {
            if (_dead) return;
            await inner.SaveHeroAsync(hero, ct);
            if (hero.ParentAId is not null) _dead = true;
        }
        public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveItemPurchaseAsync(purchase, ct);
        public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveTournamentAsync(session, ct);
        public Task SavePlayerAsync(Player player, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SavePlayerAsync(player, ct);
        public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveFancyFindAsync(find, ct);
        public Task SaveHeroProgressionAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveHeroProgressionAsync(hero, ct);
        public Task DeleteHeroAsync(string heroId, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.DeleteHeroAsync(heroId, ct);
        public Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveOfferAsync(offer, ct);
        public Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveStudProposalAsync(proposal, ct);
        public Task SaveRenameAsync(RenameSession session, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveRenameAsync(session, ct);
        public Task DeleteRenameAsync(string heroId, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.DeleteRenameAsync(heroId, ct);
        public Task SaveGauntletAsync(GauntletSession session, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveGauntletAsync(session, ct);
        public Task DeleteGauntletAsync(string gauntletId, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.DeleteGauntletAsync(gauntletId, ct);
        public Task SaveBreedingAsync(BreedingSession session, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveBreedingAsync(session, ct);
        public Task SaveMergeAsync(MergeSession session, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveMergeAsync(session, ct);
        public Task DeleteEscrowSessionAsync(string sessionId, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.DeleteEscrowSessionAsync(sessionId, ct);
        public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveHeroSaleAsync(sale, ct);
        public Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveHeroTombstoneAsync(stone, ct);
        public Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveHeroBidAsync(bid, ct);
        public Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default)
            => _dead ? Task.CompletedTask : inner.SaveTreasuryFlowAsync(id, direction, tag, sats, ct);
    }
}
