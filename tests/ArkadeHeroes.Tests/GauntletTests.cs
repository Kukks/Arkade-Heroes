using System.Security.Cryptography;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// F1 PvE gauntlet — the pure resolver + the anti-farming reward rules. The whole point is that PvE
/// XP is a bounded, fee-priced, level-capped faucet that can't seed the ladder, and the ghosts are
/// derived from the run entropy so the server can't pick soft foes and the client can replay it.
/// </summary>
public class GauntletTests
{
    private static Hero Runner(int level)
        => new() { Id = "hero", OwnerId = "p", Name = "H", Genome = Genome.NewGen0(new byte[] { 9, 9, 9 }), Level = level };

    [Fact]
    public void ResolveIsDeterministic()
    {
        var hero = Runner(5);
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData([1]), "run", "1");

        var a = Gauntlet.Resolve(hero, entropy);
        var b = Gauntlet.Resolve(hero, entropy);

        Assert.Equal(a.WavesCleared, b.WavesCleared);
        Assert.Equal(a.Waves.Count, b.Waves.Count);
        for (var i = 0; i < a.Waves.Count; i++)
        {
            Assert.Equal(a.Waves[i].Won, b.Waves[i].Won);
            Assert.Equal(a.Waves[i].GhostLevel, b.Waves[i].GhostLevel);
            Assert.Equal(a.Waves[i].Result.WinnerId, b.Waves[i].Result.WinnerId);
        }
    }

    [Fact]
    public void RunEndsAtFirstLoss()
    {
        // A level-1 hero cannot clear a ramping ghost ladder — it stops at its first loss (< 5 waves).
        var run = Gauntlet.Resolve(Runner(1), CommitReveal.DeriveEntropy(SHA256.HashData([2]), "run", "1"));
        Assert.True(run.WavesCleared < Gauntlet.WaveCount);
        Assert.False(run.Waves[^1].Won);  // the run's last recorded wave is the loss
    }

    [Fact]
    public void GhostLevelsRampFromMinusOneToPlusThree()
    {
        Assert.Equal(new[] { 4, 5, 6, 7, 8 }, Enumerable.Range(1, 5).Select(w => Gauntlet.GhostLevel(5, w)).ToArray());
        Assert.Equal(1, Gauntlet.GhostLevel(1, 1)); // floored at 1
    }

    [Fact]
    public void GhostGearScalesWithWave()
    {
        Assert.Empty(Gauntlet.GhostGear(1));
        Assert.Empty(Gauntlet.GhostGear(3));
        Assert.Equal(2, Gauntlet.GhostGear(4).Count);
        Assert.Contains("arkforged-edge", Gauntlet.GhostGear(5));
    }

    [Fact]
    public void XpSchedulesBelowCap_AndIsZeroAtOrAboveCap()
    {
        Assert.Equal(15, Gauntlet.XpForRun(1, 1));
        Assert.Equal(15 + 20 + 25 + 30 + 40, Gauntlet.XpForRun(1, 5));  // full clear = 130
        Assert.Equal(0, Gauntlet.XpForRun(1, 0));                        // cleared nothing
        // The anti-farming cap: at or past level 10 a run mints ZERO, however many waves it clears.
        Assert.Equal(0, Gauntlet.XpForRun(Gauntlet.PveXpLevelCap, 5));
        Assert.Equal(0, Gauntlet.XpForRun(25, 5));
    }

    [Fact]
    public void RewardItemOnlyOnFullClear_AndDeterministic()
    {
        var entropy = new byte[] { 7, 0, 0 };
        Assert.Null(Gauntlet.RewardItem(entropy, 4));   // partial clear → nothing
        var item = Gauntlet.RewardItem(entropy, 5);
        Assert.NotNull(item);
        Assert.Equal(item, Gauntlet.RewardItem(entropy, 5));  // deterministic in entropy
    }

    [Fact]
    public void FeeIsMatchFeePlusPremium_AndAlwaysBeatsAnyDrop()
    {
        for (var level = 1; level <= 20; level++)
        {
            Assert.Equal(Leveling.MatchFee(level) + Gauntlet.FeeBonusSats, Gauntlet.Fee(level));
            // EV firewall: entry always costs more than the best item it can drop (500-sat tier),
            // so PvE is treasury-negative at any clear rate.
            Assert.True(Gauntlet.Fee(level) > 500);
        }
    }

    [Fact]
    public void LifetimeXpFaucetIsBoundedAndShutsAtTheCap()
    {
        // The most XP PvE can EVER mint for one hero is bounded — full clears only count below the cap.
        long total = 0;
        for (var level = 1; level < Gauntlet.PveXpLevelCap; level++) total += Gauntlet.XpForRun(level, 5);
        Assert.True(total is > 0 and < 2000, $"faucet should be a small finite training band, got {total}");
        Assert.Equal(0, Gauntlet.XpForRun(Gauntlet.PveXpLevelCap, 5));  // shut at the cap
    }
}
