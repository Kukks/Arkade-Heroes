using System.Security.Cryptography;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Trials.razor quotes two rates at a recruit-holder: a bred hero clears its first wave "about half the
/// time", a recruit "about one time in forty". They are prose, so nothing else can catch them going stale —
/// and a balance change to the genome caps, the ghost ladder or the battle engine would do exactly that,
/// silently, on the one screen that exists to explain why a new player keeps losing.
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
            var hero = new Hero
            {
                Id = $"probe-{i}",
                OwnerId = "test",
                Name = "Probe",
                Genome = recruit
                    ? Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap)
                    : Genome.NewGen0(entropy),
                Level = level,
            };
            if (Trials.Resolve(hero, entropy).WavesCleared > 0) cleared++;
        }
        return (double)cleared / Samples;
    }

    [Fact]
    public void ABredHeroStillClearsItsFirstWaveAboutHalfTheTime()
    {
        var rate = FirstWaveClearRate(recruit: false, level: 1, seed: 11);

        Assert.True(rate is >= 0.40 and <= 0.65,
            $"Trials.razor tells a recruit-holder a bred hero clears wave 1 \"about half the time\"; " +
            $"measured {rate:P1} over {Samples} runs. Update the page or the balance.");
    }

    [Fact]
    public void ARecruitStillClearsItsFirstWaveAboutOneTimeInForty()
    {
        var rate = FirstWaveClearRate(recruit: true, level: 1, seed: 11);

        Assert.True(rate is >= 0.005 and <= 0.06,
            $"Trials.razor tells a recruit-holder it clears wave 1 \"about one time in forty\" (2.5%); " +
            $"measured {rate:P1} over {Samples} runs. Update the page or the balance.");
    }

    /// <summary>The claim the page rests on — the gap is what makes breeding the advice worth giving.</summary>
    [Fact]
    public void TheGapBetweenThemIsWhatThePageIsSellingAndItIsStillLarge()
    {
        var bred = FirstWaveClearRate(recruit: false, level: 1, seed: 11);
        var recruit = FirstWaveClearRate(recruit: true, level: 1, seed: 11);

        Assert.True(bred >= recruit * 8,
            $"bred {bred:P1} vs recruit {recruit:P1} — the page promises breeding is the lever here.");
    }
}
