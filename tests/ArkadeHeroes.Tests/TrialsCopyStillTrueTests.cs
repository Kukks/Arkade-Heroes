using System.Security.Cryptography;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Trials.razor quotes these rates in PROSE, so nothing else catches them going stale.
/// </summary>
public class TrialsCopyStillTrueTests
{
    private const int Samples = 4_000;

    private static double FirstWaveClearRate(bool recruit, int level, int seed)
    {
        var rng = new Random(seed);
        var cleared = 0;
        for (var i = 0; i < Samples; i++)
        {
            var e = new byte[32];
            rng.NextBytes(e);
            var entropy = SHA256.HashData(e);
            if (Trials.Resolve(HeroFor(recruit, entropy, level), entropy).WavesCleared > 0) cleared++;
        }
        return (double)cleared / Samples;
    }

    private static Hero HeroFor(bool recruit, byte[] entropy, int level) => new()
    {
        Id = "probe",
        OwnerId = "test",
        Name = "Probe",
        Genome = recruit ? Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap) : Genome.NewGen0(entropy),
        Level = level,
    };

    [Fact]
    public void ABredHeroStillNearlyAlwaysClearsItsFirstWave()
    {
        var rate = FirstWaveClearRate(recruit: false, level: 1, seed: 11);

        Assert.True(rate >= 0.90,
            $"Trials.razor tells a recruit-holder a bred hero \"nearly always\" clears wave 1; " +
            $"measured {rate:P1} over {Samples} runs. Update the page or the balance.");
    }

    [Fact]
    public void ARecruitStillClearsItsFirstWaveAboutHalfTheTime()
    {
        var rate = FirstWaveClearRate(recruit: true, level: 1, seed: 11);

        Assert.True(rate is >= 0.35 and <= 0.65,
            $"Trials.razor tells a recruit-holder it clears wave 1 \"about half the time\"; " +
            $"measured {rate:P1} over {Samples} runs. Update the page or the balance.");
    }

    [Fact]
    public void BreedingStillTakesYouSeveralTimesDeeper_WhichIsWhatThePageNowSells()
    {
        var bred = MeanWavesCleared(recruit: false, level: 1, seed: 11);
        var recruit = MeanWavesCleared(recruit: true, level: 1, seed: 11);

        Assert.True(bred >= recruit * 3,
            $"bred averaged {bred:F2} waves against a recruit's {recruit:F2} — the page promises several times deeper.");
    }

    private static double MeanWavesCleared(bool recruit, int level, int seed)
    {
        var rng = new Random(seed);
        long total = 0;
        for (var i = 0; i < Samples; i++)
        {
            var e = new byte[32];
            rng.NextBytes(e);
            var entropy = SHA256.HashData(e);
            total += Trials.Resolve(HeroFor(recruit, entropy, level), entropy).WavesCleared;
        }
        return (double)total / Samples;
    }
}
