using System.Security.Cryptography;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>Gauntlet.razor tells the player the ladder "climbs with you" so levelling leaves the odds about
/// where they were, and that "Gear is the lever". Both are measurable, so neither should sit unguarded.</summary>
public class GauntletCopyStillTrueTests
{
    private const int Samples = 2_000;

    private static double AverageWaves(int level, bool geared, int seed)
    {
        var rng = new Random(seed);
        var total = 0;
        for (var i = 0; i < Samples; i++)
        {
            var e = new byte[32];
            rng.NextBytes(e);
            var entropy = SHA256.HashData(e);
            var hero = new Hero
            {
                Id = $"probe-{i}",
                OwnerId = "test",
                Name = "Probe",
                Genome = Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap),
                Level = level,
            };
            if (geared)
                foreach (var slot in ItemCatalog.All.Select(x => x.Slot).Distinct())
                    if (ItemCatalog.All.Where(x => x.Slot == slot && x.MinLevel <= level)
                            .OrderByDescending(x => x.PriceSats).FirstOrDefault() is { } best)
                        hero.Equipment.Equip(best);
            total += Gauntlet.Resolve(hero, entropy).WavesCleared;
        }
        return (double)total / Samples;
    }

    [Fact]
    public void LevellingAloneLeavesTheOddsAboutWhereTheyWere()
    {
        var atOne = AverageWaves(1, geared: false, seed: 7);
        var atTen = AverageWaves(10, geared: false, seed: 7);

        // "About where they were" — the ghosts scale with the runner, so this is flat by construction.
        // A ratio outside a quarter either way means the mirror broke and the page is now wrong.
        Assert.True(atTen > atOne * 0.75 && atTen < atOne * 1.25,
            $"Gauntlet.razor says levelling leaves the odds about where they were; measured {atOne:F2} " +
            $"waves at level 1 against {atTen:F2} at level 10. Update the page or the ladder.");
    }

    [Fact]
    public void GearIsTheLeverAtEveryLevel_AndCompoundsWithIt()
    {
        var (bare1, kitted1) = (AverageWaves(1, false, 7), AverageWaves(1, true, 7));
        var (bare10, kitted10) = (AverageWaves(10, false, 7), AverageWaves(10, true, 7));

        // "Gear is the lever" is claimed generally, not at one level — and the shop gates gear by level, so
        // a level-1 hero wears strictly less of it. Both ends, not just the flattering one.
        Assert.True(kitted1 >= bare1 * 1.5,
            $"Gauntlet.razor calls gear the lever; at level 1 it moved {bare1:F2} waves to {kitted1:F2}.");
        Assert.True(kitted10 >= bare10 * 2.0,
            $"Gauntlet.razor calls gear the lever; at level 10 it moved {bare10:F2} waves to {kitted10:F2}.");

        // And "it compounds with level" — the page's actual wording, which neither figure alone tests.
        Assert.True(kitted10 / bare10 > kitted1 / bare1,
            $"Gauntlet.razor says gear compounds with level; the multiple went {kitted1 / bare1:F2}x at " +
            $"level 1 to {kitted10 / bare10:F2}x at level 10.");
    }
}
