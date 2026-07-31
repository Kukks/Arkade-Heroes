using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// What survives a hero's DESTRUCTION.
///
/// <para>The data-model fact these tests are built on: a burned hero is HARD DELETED. Every burn site
/// removes it from <c>GameStore.Heroes</c> and calls <c>DeleteHeroAsync</c>, because a rehydrated ghost
/// would be a fightable, listable hero whose on-chain asset is retired. So there is no row to hang a
/// "destroyed" flag on, and nothing the UI can read back — which is why nothing anywhere showed a hero as
/// destroyed. The fix is a durable HEADSTONE written at the burn site, before the erase; these tests pin
/// that it is written, that it is public, that it names what nothing else can, and that the hero's own
/// page still resolves afterwards instead of erroring.</para>
/// </summary>
public class HeroTombstoneTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public HeroTombstoneTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // ── The data-model fact itself ─────────────────────────────────────────

    [Fact]
    public async Task ADestroyedHeroIsReallyGone_ButItsHeadstoneNamesIt()
    {
        // Both halves matter. The erase is deliberate and must stay; the headstone is what makes the erase
        // survivable for a UI. If either assertion flips, the feature is broken in one direction or other.
        var (alice, _) = await _factory.RegisterAsync("Grave-Merge-A");
        var heroes = await alice.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        var baseId = heroes[0].Id;
        var sacId = heroes[1].Id;
        var sacName = store.Heroes[sacId].Name;
        var sacGenome = store.Heroes[sacId].Genome.ToHex();
        var sacLevel = store.Heroes[sacId].Level;

        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(baseId, sacId));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        var reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("grave-nonce"));

        // The row is GONE — in memory and from the roster.
        Assert.False(store.Heroes.ContainsKey(sacId));
        Assert.DoesNotContain(await alice.Heroes.MineAsync(), h => h.Id == sacId);

        // …and the headstone knows everything the row did.
        var grave = await alice.Heroes.TombstoneAsync(sacId);
        Assert.Equal(sacId, grave.HeroId);
        Assert.Equal(sacName, grave.Name);
        Assert.Equal(sacGenome, grave.GenomeHex);
        Assert.Equal(sacLevel, grave.Level);
        Assert.Equal("merge-input", grave.Reason);
        Assert.Equal(commit.MergeId, grave.SessionId);
        Assert.Equal(reveal.Hero.Id, grave.ReplacedByHeroId);
        Assert.True(grave.DestroyedAtUnixSeconds > 0);
    }

    [Fact]
    public async Task ALivingHeroHasNoHeadstone_AndAnUnknownIdHasNoneEither()
    {
        // "Destroyed" has to be distinguishable from "alive" and from "never existed" — three different
        // facts, and a headstone that answered for a living hero would mark the whole roster dead.
        var (alice, _) = await _factory.RegisterAsync("Grave-Alive-A");
        var alive = (await alice.ClaimStartersAsync())[0].Id;

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Heroes.TombstoneAsync(alive));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Heroes.TombstoneAsync("hero_does_not_exist"));
    }

    [Fact]
    public async Task ADeathMatchLoserGetsAHeadstone_NamingTheMatchThatKilledIt()
    {
        var (alice, _) = await _factory.RegisterAsync("Grave-DM-A");
        var (bob, _) = await _factory.RegisterAsync("Grave-DM-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;
        var store = _factory.Services.GetRequiredService<GameStore>();
        var names = new Dictionary<string, string>
        {
            [aliceHero] = store.Heroes[aliceHero].Name,
            [bobHero] = store.Heroes[bobHero].Name,
        };

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("grave-dm"));

        var grave = await alice.Heroes.TombstoneAsync(settle.LoserHeroId);
        Assert.Equal(names[settle.LoserHeroId], grave.Name);
        Assert.Equal("deathmatch-loser", grave.Reason);
        Assert.Equal(open.DeathMatchId, grave.SessionId);
        // A classic death-match loser simply ends — nothing rose from it, and saying otherwise would be a lie.
        Assert.Null(grave.ReplacedByHeroId);

        // The WINNER is alive, so it has none. The pair is the point: one match, two heroes, one grave.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Heroes.TombstoneAsync(settle.WinnerHeroId));
    }

    // ── The page that used to be an error ──────────────────────────────────

    [Fact]
    public async Task ADestroyedHerosOwnPageStillResolves_AndSaysHowItDied()
    {
        // Before the headstone this 404'd (the timeline) and 400'd (the hero), so the page a player lands
        // on after losing a hero read "couldn't load this hero" — an error about the game's central stake.
        var (alice, _) = await _factory.RegisterAsync("Grave-Page-A");
        var heroes = await alice.ClaimStartersAsync();
        var baseId = heroes[0].Id;
        var sacId = heroes[1].Id;

        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(baseId, sacId));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        var reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("page-nonce"));

        // The hero endpoint still cannot serve it — there IS no hero — and that is the honest answer.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Heroes.GetAsync(sacId));

        // But its history resolves, and it ends with the destruction.
        var timeline = await alice.Heroes.TimelineAsync(sacId);
        Assert.Equal(sacId, timeline.HeroId);
        var death = Assert.Single(timeline.Events, e => e.Kind == "destroyed");
        Assert.Contains("fusion", death.Summary, StringComparison.OrdinalIgnoreCase);
        // …and it names what rose from it, so the trail continues rather than stopping at a dead end.
        Assert.Contains(death.Related, r => r.HeroId == reveal.Hero.Id);
    }

    [Fact]
    public async Task ATimelineNamingADestroyedHero_MarksItDestroyedAndStillNamesIt()
    {
        // The fused CHILD's page names both its burned inputs. It used to render them as a bare id with a
        // null name, because a null name was the only way "gone" could be expressed. Now the ref carries
        // BOTH facts — destroyed, and called this — which is what lets the page link the headstone.
        var (alice, _) = await _factory.RegisterAsync("Grave-Ref-A");
        var heroes = await alice.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        var baseId = heroes[0].Id;
        var sacId = heroes[1].Id;
        var baseName = store.Heroes[baseId].Name;

        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(baseId, sacId));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        var reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("ref-nonce"));

        var childTimeline = await alice.Heroes.TimelineAsync(reveal.Hero.Id);
        var birth = Assert.Single(childTimeline.Events, e => e.Kind == "fused");
        var burnedParent = Assert.Single(birth.Related, r => r.HeroId == baseId);
        Assert.True(burnedParent.Destroyed, "a burned parent must be MARKED destroyed, not merely nameless");
        Assert.Equal(baseName, burnedParent.Name);
        // …and the prose names it too, rather than saying "a burned hero (a1b2c3…)".
        Assert.Contains(baseName, birth.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALivingHeroInATimelineIsNotMarkedDestroyed()
    {
        // The other direction of the same flag: `Destroyed` must not be a synonym for "I couldn't find it".
        var (alice, _) = await _factory.RegisterAsync("Grave-Living-A");
        var heroes = await alice.ClaimStartersAsync();
        var (_, reveal) = await alice.BreedAsync(heroes[0].Id, heroes[1].Id, "living-nonce");

        var timeline = await alice.Heroes.TimelineAsync(reveal.Hero.Id);
        var birth = Assert.Single(timeline.Events, e => e.Kind == "bred");
        Assert.All(birth.Related, r =>
        {
            Assert.False(r.Destroyed);
            Assert.NotNull(r.Name);
        });
    }

    [Fact]
    public async Task ADestroyedHerosHeadstoneKeepsItsOwnLineage()
    {
        // A grave is still a node in the family tree — the hero it was bred from, and the hero bred from
        // it, are both real relationships. The birth RECEIPT knows the parents but lives in memory; the
        // headstone's columns are the durable half.
        //
        // Cooldown zeroed so ONE pair of starters can produce two children in a row: the subject here is
        // the headstone's lineage columns, and a cooldown wait would be scaffolding, not the test.
        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Game:BreedingCooldownBaseUnit", "00:00:00"));
        var (alice, _) = await factory.RegisterAsync("Grave-Lineage-A");
        var heroes = await alice.ClaimStartersAsync();
        var (_, child) = await alice.BreedAsync(heroes[0].Id, heroes[1].Id, "lineage-parent");

        // Burn the CHILD, which unlike a starter has real parents. The sacrifice is a second child rather
        // than a starter, so the burned hero is the one whose lineage is being asserted.
        var (_, spare) = await alice.BreedAsync(heroes[0].Id, heroes[1].Id, "lineage-spare");
        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(child.Hero.Id, spare.Hero.Id));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("lineage-nonce"));

        var grave = await alice.Heroes.TombstoneAsync(child.Hero.Id);
        Assert.Equal(heroes[0].Id, grave.ParentAId);
        Assert.Equal(heroes[1].Id, grave.ParentBId);
        Assert.Equal(child.Hero.Generation, grave.Generation);
    }
}

/// <summary>
/// A headstone has to survive a restart, or the name it carries is only good until the next deploy — and
/// the whole point of it is that it is the ONLY thing left of a hero whose row was erased on purpose.
/// Drives a real restart: a second host, a fresh GameStore, the same database file.
/// </summary>
public class HeroTombstoneDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:BreedingFeeSats", "0");
            b.UseSetting("Game:MergeFeeSats", "0");
        });

    [Fact]
    public async Task AHeadstoneSurvivesARestart_WhileTheHeroStaysErased()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-grave-{Guid.NewGuid():N}.db");
        try
        {
            string burnedId, burnedName, fusedId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Grave-Durable-A");
                var heroes = await alice.ClaimStartersAsync();
                var store = first.Services.GetRequiredService<GameStore>();
                burnedId = heroes[1].Id;
                burnedName = store.Heroes[burnedId].Name;

                var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, burnedId));
                await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
                fusedId = (await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("durable"))).Hero.Id;
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            var client = new ArkadeHeroesClient(restarted.CreateClient());
            var store2 = restarted.Services.GetRequiredService<GameStore>();

            // The erase held — a restart must not resurrect a hero whose asset is retired.
            Assert.False(store2.Heroes.ContainsKey(burnedId));
            // …and the headstone came back with it, which is the only reason anything can still name it.
            var grave = await client.Heroes.TombstoneAsync(burnedId);
            Assert.Equal(burnedName, grave.Name);
            Assert.Equal("merge-input", grave.Reason);
            Assert.Equal(fusedId, grave.ReplacedByHeroId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
