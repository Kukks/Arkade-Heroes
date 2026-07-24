using ArkadeHeroes.Chain;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Hero durability. Heroes were the last major aggregate living only in memory under a configured
/// <c>Game:StateDbPath</c>: the chain can't enumerate them back (IChainService has no hero listing), so a
/// restart lost EVERY hero — players kept their identities and their paid purchases but not the characters
/// those exist for, and every open tournament stranded. These tests drive a REAL restart (a second host, a
/// fresh GameStore, the same database file) over the hybrid save strategy: IDENTITY events (mint, burn,
/// transfer, rename) persist immediately; PROGRESSION (level/XP, equipment, cooldowns) rides a periodic
/// dirty-flush, accepting a bounded window of loss.
/// </summary>
public class HeroPersistenceTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"arkade-hero-durability-{tag}-{Guid.NewGuid():N}.db");

    private static void CleanupDb(string dbPath)
    {
        // SQLite pools connections, so the file stays handled until the pool is cleared. A leftover temp
        // file is harmless either way — never fail a durability test on its own housekeeping.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    [Fact]
    public async Task MintedHeroes_SurviveARestart_WithIdentityAndStartingState()
    {
        var dbPath = TempDb("mint");
        try
        {
            string playerId;
            var minted = new List<(string Id, string Name, string GenomeHex, string? ServerSeedHex, string? AssetId, string? MintArkTxId)>();
            using (var first = HostOn(dbPath))
            {
                var (alice, dto) = await first.RegisterAsync("Hero-Durable");
                playerId = dto.PlayerId;
                await alice.ClaimStartersAsync();
                // Capture the full server-side state (the DTO omits the audit fields the schema must keep).
                foreach (var h in first.Services.GetRequiredService<GameStore>().Heroes.Values)
                    minted.Add((h.Id, h.Name, h.Genome.ToHex(), h.ServerSeedHex, h.AssetId, h.MintArkTxId));
                Assert.Equal(2, minted.Count);
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            foreach (var (id, name, genomeHex, serverSeedHex, assetId, mintArkTxId) in minted)
            {
                Assert.True(store.Heroes.TryGetValue(id, out var hero),
                    "a minted hero must survive a restart — the chain can't re-enumerate it and the player owns it");
                Assert.Equal(playerId, hero!.OwnerId);
                Assert.Equal(name, hero.Name);
                Assert.Equal(genomeHex, hero.Genome.ToHex());   // the genome IS the hero — byte-identical or it's a different character
                Assert.Equal(0, hero.Generation);
                Assert.Null(hero.ParentAId);
                Assert.Equal(serverSeedHex, hero.ServerSeedHex);   // the commit–reveal audit trail survives with it
                Assert.Equal(assetId, hero.AssetId);
                Assert.Equal(mintArkTxId, hero.MintArkTxId);
                Assert.Equal(1, hero.Level);                    // starting progression, exactly as minted
                Assert.Equal(0, hero.Xp);
                Assert.Equal(0, hero.BreedCount);
                Assert.Empty(hero.Equipment.Slots);
            }
        }
        finally { CleanupDb(dbPath); }
    }

    [Fact]
    public async Task MergeBurn_DeletesTheInputsDurably_AndTheFusedHeroSurvives()
    {
        var dbPath = TempDb("burn");
        try
        {
            string baseId, sacrificeId, fusedId;
            int fusedLevel;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Burn-Durable");
                var heroes = await alice.ClaimStartersAsync();
                (baseId, sacrificeId) = (heroes[0].Id, heroes[1].Id);
                var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(baseId, sacrificeId));
                await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
                var reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("burn-nonce"));
                (fusedId, fusedLevel) = (reveal.Hero.Id, reveal.Hero.Level);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            // The fused hero survives WITH its inherited level (birth state, saved at the identity event) —
            // and the burned inputs are durably GONE: their assets are on-chain-retired, so rehydrating one
            // would resurrect a hero whose asset no longer exists (fightable, listable, a phantom).
            Assert.True(store.Heroes.TryGetValue(fusedId, out var fused));
            Assert.Equal(fusedLevel, fused!.Level);
            Assert.False(store.Heroes.ContainsKey(baseId), "a merged-away hero must not rise from the database");
            Assert.False(store.Heroes.ContainsKey(sacrificeId), "a merged-away hero must not rise from the database");
        }
        finally { CleanupDb(dbPath); }
    }

    [Fact]
    public async Task TransferAndRename_SurviveARestart()
    {
        var dbPath = TempDb("identity");
        try
        {
            string giftId, keptId, bobId;
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.UseSetting("Game:HeroRenameFeeSats", "0");   // the fee flow isn't under test — the durable Name is
            }))
            {
                var (alice, _) = await first.RegisterAsync("Rename-Durable");
                var (_, bob) = await first.RegisterAsync("Transfer-Recipient");
                bobId = bob.PlayerId;
                var heroes = await alice.ClaimStartersAsync();
                (giftId, keptId) = (heroes[0].Id, heroes[1].Id);

                // Transfer: the (simulated) client wallet moves the asset, then the server verifies + reassigns.
                await alice.TransferAssetAsync(heroes[0].AssetId!, bobId);
                await alice.Heroes.TransferAsync(giftId, new TransferRequest(bobId));

                // Rename: free (fee 0), so request + confirm applies immediately.
                await alice.Heroes.RequestRenameAsync(keptId, new RenameHeroRequest("Grimspark"));
                await alice.Heroes.ConfirmRenameAsync(keptId);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            // Mis-owning across a restart is the catastrophic case: the gift snapping back to Alice would
            // let her fight/list/sell a hero whose asset Bob's wallet holds.
            Assert.Equal(bobId, store.Heroes[giftId].OwnerId);
            Assert.Empty(store.Heroes[giftId].Equipment.Slots);   // gear stayed with the sender, durably too
            // The paid unique name is bought identity — a restart reverting it to the derived name loses a purchase.
            Assert.Equal("Grimspark", store.Heroes[keptId].Name);
        }
        finally { CleanupDb(dbPath); }
    }

    [Fact]
    public async Task FlushedProgression_SurvivesARestart()
    {
        var dbPath = TempDb("flush");
        try
        {
            string heroId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Flush-Durable");
                var heroes = await alice.ClaimStartersAsync();
                heroId = heroes[0].Id;
                await alice.BuyItemAsync("rusty-blade");
                await alice.Heroes.EquipAsync(heroId, new EquipRequest("rusty-blade"));   // marks the hero dirty

                // Level/XP mutate through the same dirty-set contract (ApplyXp marks internally); poke them
                // directly so this test doesn't drag a whole fight flow in just to move two integers.
                var hero = first.Services.GetRequiredService<GameStore>().Heroes[heroId];
                (hero.Level, hero.Xp) = (4, 250);
                first.Services.GetRequiredService<GameStore>().MarkHeroDirty(heroId);

                // Force a deterministic flush instead of racing the 15s timer.
                await first.Services.GetRequiredService<ArkadeHeroes.Server.Persistence.HeroFlushService>()
                    .FlushDirtyHeroesAsync();
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            var recovered = store.Heroes[heroId];
            Assert.Equal(4, recovered.Level);
            Assert.Equal(250, recovered.Xp);
            Assert.Contains("rusty-blade", recovered.Equipment.Slots.Values);   // the loadout rebuilt from its stored ids
        }
        finally { CleanupDb(dbPath); }
    }

    [Fact]
    public async Task CrashBeforeFlush_KeepsTheHero_AtItsLastSavedProgression()
    {
        // The bounded-loss half of the hybrid contract: progression mutated AFTER the last flush dies with
        // the process, but the HERO does not — identity was saved inline at mint. This is the deliberate
        // trade (a flush per XP tick would put a database write on every fight), so assert exactly what
        // the design promises: the hero exists at its last-saved state, not that the latest grind survived.
        var dbPath = TempDb("crash");
        var processDied = false;
        try
        {
            string heroId;
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                // The crash: from the flip onward every write silently drops — the deterministic stand-in
                // for a process that dies before the periodic flush runs (mirrors DailyDurabilityGuardTests).
                b.ConfigureTestServices(s => s.AddSingleton<ArkadeHeroes.Server.Persistence.IGameStatePersistence>(sp =>
                    new CrashWindowPersistence(
                        new ArkadeHeroes.Server.Persistence.SqliteGameStatePersistence(
                            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ArkadeHeroes.Server.Persistence.GameStateDbContext>>()),
                        () => processDied)));
            }))
            {
                var (alice, _) = await first.RegisterAsync("Crash-Window");
                var heroes = await alice.ClaimStartersAsync();   // mint saves land — the hero is durable
                heroId = heroes[0].Id;
                await alice.BuyItemAsync("rusty-blade");         // bought while alive; equipping comes after death

                processDied = true;   // ── the process is now "dead": nothing below ever reaches disk ──
                await alice.Heroes.EquipAsync(heroId, new EquipRequest("rusty-blade"));
                var hero = first.Services.GetRequiredService<GameStore>().Heroes[heroId];
                (hero.Level, hero.Xp) = (7, 999);
                first.Services.GetRequiredService<GameStore>().MarkHeroDirty(heroId);
                // No flush is invoked — and even if the timer fired here, the dead persistence drops it.
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Heroes.TryGetValue(heroId, out var recovered),
                "the hero itself must survive — identity persisted at mint, before the crash");
            Assert.Equal(1, recovered!.Level);          // the un-flushed grind is gone — the accepted window
            Assert.Equal(0, recovered.Xp);
            Assert.Empty(recovered.Equipment.Slots);    // the post-death equip died with the process
        }
        finally { CleanupDb(dbPath); }
    }

    /// <summary>Delegates to the real SQLite persistence until <paramref name="processDied"/> flips, then
    /// silently drops every write — the deterministic stand-in for a process crash at that instant (mirrors
    /// DailyDurabilityGuardTests): nothing issued after the moment of death ever reaches disk.</summary>
    private sealed class CrashWindowPersistence(
        ArkadeHeroes.Server.Persistence.IGameStatePersistence inner, Func<bool> processDied)
        : ArkadeHeroes.Server.Persistence.IGameStatePersistence
    {
        public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => inner.LoadIntoAsync(store, ct);
        public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveItemPurchaseAsync(purchase, ct);
        public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveTournamentAsync(session, ct);
        public Task SavePlayerAsync(Player player, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SavePlayerAsync(player, ct);
        public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveFancyFindAsync(find, ct);
        public Task SaveHeroAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.SaveHeroAsync(hero, ct);
        public Task DeleteHeroAsync(string heroId, CancellationToken ct = default)
            => processDied() ? Task.CompletedTask : inner.DeleteHeroAsync(heroId, ct);
    }

    /// <summary>
    /// KEYSTONE — the tournament strand, end to end. Resolve fights from the FILL-time locked snapshots
    /// (#104), which are deliberately never persisted, so a full bracket that crosses a restart can never
    /// resolve — with or without its heroes. Hero persistence therefore must NOT strand the paid buy-ins:
    /// the refund gate keys on the same snapshots resolve does (the heroes' presence is irrelevant to
    /// resolvability post-#104), so the rehydrated bracket keeps its heroes, refuses to resolve, and
    /// REFUNDS every paid buy-in. Before the gate keyed on snapshots it keyed on missing heroes — which
    /// hero persistence made never-missing, leaving a restart-crossing bracket unresolvable AND
    /// unrefundable, real sats parked forever.
    /// </summary>
    [Fact]
    public async Task FullBracket_AfterRestart_KeepsItsHeroes_AndRefundsTheStrandedBuyIns()
    {
        var dbPath = TempDb("tournament");
        // ONE chain instance spans both hosts — the chain is the outside world and survives a server
        // restart, so the buy-ins paid before the bounce still read as paid after it (without this the
        // resolve refusal would fire on the earlier unpaid-buy-in check and prove nothing about snapshots).
        var chain = new InMemoryChainService();
        try
        {
            string tid, openerId;
            long treasuryStart;
            var entrantHeroIds = new List<string>();
            using (var first = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            }))
            {
                var players = new List<(ArkadeHeroes.Client.Sdk.ArkadeHeroesClient Client, string PlayerId, string HeroId)>();
                for (var i = 0; i < 4; i++)
                {
                    var (c, dto) = await first.RegisterAsync($"Zombie-P{i}");
                    var heroes = await c.ClaimStartersAsync();
                    players.Add((c, dto.PlayerId, heroes[0].Id));
                    entrantHeroIds.Add(heroes[0].Id);
                }
                openerId = players[0].PlayerId;
                treasuryStart = await chain.TreasuryBalanceAsync();   // after registrations/mints, before the pot
                var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, 1000, 4));
                tid = open.Tournament.Id;
                await players[0].Client.PayInvoiceAsync(open.BuyIn.InvoiceId);
                for (var i = 1; i < 4; i++)
                {
                    var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
                    await players[i].Client.PayInvoiceAsync(join.BuyIn.InvoiceId);
                }
            }

            using var restarted = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("Game:StateDbPath", dbPath);
                b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain));
            });
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();
            var svc = restarted.Services.GetRequiredService<GameService>();

            // The bracket AND all four entrant heroes survived the restart…
            Assert.Equal("full", store.Tournaments[tid].Status);
            foreach (var heroId in entrantHeroIds)
                Assert.True(store.Heroes.ContainsKey(heroId), "entrant heroes must be rehydrated");

            // …but resolve still refuses (the fill-time snapshots its commitment binds are gone — #104)…
            var resolveRefusal = await Assert.ThrowsAsync<GameRuleException>(
                () => svc.ResolveTournamentAsync(store.Players[openerId], tid, "post-restart", CancellationToken.None));
            Assert.Contains("snapshots", resolveRefusal.Message);

            // …so the strand refund fires: the bracket can never honor its commitment again, and every
            // paid buy-in comes back. Net-zero at the chain — the pot in, the pot out, no rake on a refund.
            var (session, refunded, refundedSats) = await svc.RefundTournamentAsync(tid, CancellationToken.None);
            Assert.Equal("refunded", session.Status);
            Assert.Equal(4, refunded);
            Assert.Equal(4 * 1000L, refundedSats);
            Assert.Equal(treasuryStart, await chain.TreasuryBalanceAsync());
        }
        finally { CleanupDb(dbPath); }
    }
}
