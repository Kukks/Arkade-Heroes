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
}
