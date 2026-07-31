using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Player achievements are DERIVED on read from the roster + resolved tournaments (no per-event tracking),
/// with a badge unlocked at each milestone. Pure over in-memory state, so fully deterministic.
/// </summary>
public class AchievementsTests
{
    [Fact]
    public async Task Achievements_FreshPlayerWithStarters_HasNoMilestoneBadges()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("Ach-Fresh");
        await player.ClaimStartersAsync();   // two gen-0, trait-less starters

        var a = await player.Players.AchievementsAsync();
        Assert.Equal(2, a.HeroesOwned);
        Assert.Equal(0, a.HeroesBred);       // gen-0 starters aren't bred
        Assert.Empty(a.Badges);              // nothing unlocked yet
    }

    [Fact]
    public async Task Achievements_MilestonesUnlockBadges()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, playerDto) = await factory.RegisterAsync("Ach-Badges");
        var store = factory.Services.GetRequiredService<GameStore>();

        static Genome Legendary() { var g = new byte[32]; g[16 + (int)TraitCategory.Aura * 2] = 255; return new Genome(g); }

        // Five heroes: three "bred" (gen > 0), one a Legendary-Aura genome.
        for (var i = 0; i < 5; i++)
            store.Heroes[$"ach-{i}"] = new Hero
            {
                Id = $"ach-{i}", OwnerId = playerDto.PlayerId, Name = $"H{i}", Level = 1,
                Genome = i == 0 ? Legendary() : new Genome(new byte[32]),
                Generation = i < 3 ? 1 : 0,
            };

        var a = await player.Players.AchievementsAsync();
        Assert.Equal(5, a.HeroesOwned);
        Assert.Equal(3, a.HeroesBred);
        Assert.Equal(1, a.Legendaries);
        Assert.Contains("Collector", a.Badges);       // 5 owned
        Assert.Contains("Breeder", a.Badges);         // 3 bred
        Assert.Contains("Legend-keeper", a.Badges);   // a Legendary in the roster
        Assert.DoesNotContain("Champion", a.Badges);  // no tournament win
    }

    [Fact]
    public async Task Achievements_FancyCollection_ListsTheOwnedSets()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, playerDto) = await factory.RegisterAsync("Ach-Fancy");
        var store = factory.Services.GetRequiredService<GameStore>();

        // "Emberlord" = Aura AND Sigil at Epic+ (byte 255 → Legendary tier); only two Legendaries, so not "Sovereign".
        var g = new byte[32];
        g[16 + (int)TraitCategory.Aura * 2] = 255;
        g[16 + (int)TraitCategory.Sigil * 2] = 255;
        store.Heroes["ember"] = new Hero
        {
            Id = "ember", OwnerId = playerDto.PlayerId, Name = "Ember", Level = 1,
            Genome = new Genome(g), Generation = 1,
        };

        var a = await player.Players.AchievementsAsync();
        Assert.Contains("Emberlord", a.FancySetsOwned);
        Assert.DoesNotContain("Sovereign", a.FancySetsOwned);   // two Legendaries isn't the ultra-grail
    }

    // Discovering a Fancy set is a permanent DEED, credited once to its first finder. It must not be a
    // function of who holds the #1-edition hero right now — otherwise the badge is buyable (acquire a #1
    // and inherit the glory) and revocable (sell the set you discovered and lose it). The discovery record
    // already stamps the discoverer, so the badge should follow the deed, not the asset.
    [Fact]
    public async Task Trailblazer_FollowsTheDiscoverer_NotWhoeverHoldsTheNumberOneEditionNow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (discoverer, discovererDto) = await factory.RegisterAsync("Ach-Trail");
        var (buyer, buyerDto) = await factory.RegisterAsync("Ach-Buyer");
        var store = factory.Services.GetRequiredService<GameStore>();

        // A hero that expresses a Fancy set (Emberlord = Aura AND Sigil at Legendary), owned by the
        // discoverer and stamped edition #1 — they were first to breed it.
        var g = new byte[32];
        g[16 + (int)TraitCategory.Aura * 2] = 255;
        g[16 + (int)TraitCategory.Sigil * 2] = 255;
        store.Heroes["ember-1"] = new Hero
        {
            Id = "ember-1", OwnerId = discovererDto.PlayerId, Name = "Ember", Level = 1,
            Genome = new Genome(g), Generation = 1,
        };
        store.RecordFancyFind("Emberlord", "ember-1", "Ember", discovererDto.PlayerId, 100);

        Assert.Contains("Trailblazer", (await discoverer.Players.AchievementsAsync()).Badges);

        // They sell the #1-edition hero to the buyer. The deed of discovery stays with the discoverer…
        store.Heroes["ember-1"].OwnerId = buyerDto.PlayerId;

        Assert.Contains("Trailblazer", (await discoverer.Players.AchievementsAsync()).Badges);
        // …and does NOT transfer to the buyer, who merely holds the hero and discovered nothing.
        Assert.DoesNotContain("Trailblazer", (await buyer.Players.AchievementsAsync()).Badges);
    }

    /// <summary>
    /// The other side of the Trailblazer exception: every OTHER badge is a read of the roster you hold
    /// right now, so it goes away when the hero does.
    ///
    /// <para>/achievements used to tell players "nothing can be taken away", which is the opposite of
    /// this. Pinned as behaviour rather than as prose so the page's copy has something to be checked
    /// against — <c>PageClaimTests.TheAchievementsPage_DoesNotClaimBadgesArePermanent</c> is the half
    /// that reads the page.</para>
    /// </summary>
    [Fact]
    public async Task ABadgeEarnedByOwning_IsLostWhenTheHeroLeavesTheRoster()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, playerDto) = await factory.RegisterAsync("Ach-Revoke");
        var (other, otherDto) = await factory.RegisterAsync("Ach-Revoke-Other");
        var store = factory.Services.GetRequiredService<GameStore>();

        static Genome Legendary() { var g = new byte[32]; g[16 + (int)TraitCategory.Aura * 2] = 255; return new Genome(g); }

        for (var i = 0; i < 5; i++)
            store.Heroes[$"rev-{i}"] = new Hero
            {
                Id = $"rev-{i}", OwnerId = playerDto.PlayerId, Name = $"R{i}", Level = 1,
                Genome = i == 0 ? Legendary() : new Genome(new byte[32]),
                Generation = i < 3 ? 1 : 0,
            };

        var earned = await player.Players.AchievementsAsync();
        Assert.Contains("Collector", earned.Badges);
        Assert.Contains("Breeder", earned.Badges);
        Assert.Contains("Legend-keeper", earned.Badges);

        // Sell the Legendary and one bred hero; burn a third (a fusion sacrifice, a lost death-match).
        store.Heroes["rev-0"].OwnerId = otherDto.PlayerId;
        store.Heroes["rev-1"].OwnerId = otherDto.PlayerId;
        store.Heroes.TryRemove("rev-2", out _);

        var after = await player.Players.AchievementsAsync();
        Assert.Equal(2, after.HeroesOwned);
        Assert.DoesNotContain("Collector", after.Badges);      // below five owned
        Assert.DoesNotContain("Breeder", after.Badges);        // below three bred heroes owned
        Assert.DoesNotContain("Legend-keeper", after.Badges);  // the Legendary is somebody else's now

        // And they land on whoever holds the heroes, which is the same fact from the other end: these
        // badges track holdings, not deeds.
        Assert.Contains("Legend-keeper", (await other.Players.AchievementsAsync()).Badges);
    }
}
