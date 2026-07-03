using System.Security.Cryptography;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

public class BattleEngineTests
{
    private static Hero MakeHero(string id, byte statGenes, int level = 5)
    {
        var bytes = new byte[32];
        for (var i = 0; i < 5; i++) bytes[i] = statGenes;
        bytes[5] = 0;            // all Ember: neutral matchup
        bytes[6] = 0; bytes[7] = 15;
        var genome = new Genome(bytes);
        return new Hero
        {
            Id = id,
            OwnerId = "tester",
            Name = HeroNamer.DeriveName(genome),
            Genome = genome,
            Level = level,
        };
    }

    [Fact]
    public void FightIsDeterministic()
    {
        var seed = SHA256.HashData([1, 2, 3]);
        var r1 = BattleEngine.Fight(MakeHero("a", 100), MakeHero("b", 120), seed);
        var r2 = BattleEngine.Fight(MakeHero("a", 100), MakeHero("b", 120), seed);

        Assert.Equal(r1.WinnerId, r2.WinnerId);
        Assert.Equal(r1.Turns, r2.Turns);
        Assert.Equal(r1.Events.Count, r2.Events.Count);
        for (var i = 0; i < r1.Events.Count; i++)
            Assert.Equal(r1.Events[i], r2.Events[i]);
    }

    [Fact]
    public void DifferentSeedsCanDiverge()
    {
        // Evenly matched heroes: over many seeds both must win at least once.
        var aWins = 0;
        var bWins = 0;
        for (var n = 0; n < 40; n++)
        {
            var seed = SHA256.HashData(BitConverter.GetBytes(n));
            var result = BattleEngine.Fight(MakeHero("a", 128), MakeHero("b", 128, level: 5), seed);
            if (result.WinnerId == "a") aWins++; else bWins++;
        }
        Assert.True(aWins > 0 && bWins > 0, $"Expected both to win sometimes (a={aWins}, b={bWins}).");
    }

    [Fact]
    public void OverwhelminglyStrongerHeroWins()
    {
        var strong = MakeHero("strong", 255, level: 20);
        var weak = MakeHero("weak", 10, level: 1);
        for (var n = 0; n < 10; n++)
        {
            var seed = SHA256.HashData(BitConverter.GetBytes(1000 + n));
            var result = BattleEngine.Fight(strong, weak, seed);
            Assert.Equal("strong", result.WinnerId);
        }
    }

    [Fact]
    public void BattleProducesReplayableLog()
    {
        var seed = SHA256.HashData([9]);
        var result = BattleEngine.Fight(MakeHero("a", 100), MakeHero("b", 100), seed);

        Assert.NotEmpty(result.Events);
        Assert.NotEqual(result.WinnerId, result.LoserId);
        Assert.True(result.Turns is >= 1 and <= BattleEngine.MaxTurns);
        // The log ends with a decisive event.
        Assert.Contains(result.Events[^1].Kind,
            new[] { BattleEventKind.Defeated, BattleEventKind.TimeoutDecision });
    }

    [Fact]
    public void SelfFightIsRejected()
    {
        var hero = MakeHero("same", 100);
        Assert.Throws<ArgumentException>(() =>
            BattleEngine.Fight(hero, MakeHero("same", 120), new byte[32]));
    }

    [Fact]
    public void ElementRingMultipliers()
    {
        Assert.Equal(ElementMatrix.Strong, ElementMatrix.Multiplier(Element.Ember, Element.Gale));
        Assert.Equal(ElementMatrix.Weak, ElementMatrix.Multiplier(Element.Gale, Element.Ember));
        Assert.Equal(ElementMatrix.Neutral, ElementMatrix.Multiplier(Element.Ember, Element.Terra));
        Assert.Equal(ElementMatrix.Strong, ElementMatrix.Multiplier(Element.Umbral, Element.Ember)); // ring wraps
    }
}
