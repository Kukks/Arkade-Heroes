using System.Security.Cryptography;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Skills;

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

    // A hero whose gene-A skill is a specific catalog entry (SkillGeneA = Bytes[6] → GeneSkills[byte % 16]).
    private static Hero HeroWithGeneSkill(string id, byte geneAByte, int level = 5, byte statGenes = 128)
    {
        var bytes = new byte[32];
        for (var i = 0; i < 5; i++) bytes[i] = statGenes;
        bytes[5] = 0;            // Ember element (neutral matchup for these tests)
        bytes[6] = geneAByte;    // gene-A skill index
        bytes[7] = 15;           // gene-B = twin-fangs (only unlocks at level 6)
        var genome = new Genome(bytes);
        return new Hero { Id = id, OwnerId = "tester", Name = HeroNamer.DeriveName(genome), Genome = genome, Level = level };
    }

    [Fact]
    public void GeneSkillIsKnownFromLevelOne()
    {
        // No more Strike-only starters: gene-A is learned at level 1 under the default combat config.
        var hero = HeroWithGeneSkill("h", geneAByte: 1, level: 1);  // gene-A = Ember Lash (GeneSkills[1])
        var skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level);
        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Id == "strike");
        Assert.Contains(skills, s => s.Id == "ember-lash");
        Assert.DoesNotContain(skills, s => s.Id == "twin-fangs"); // gene-B still gated to level 6
    }

    [Fact]
    public void SkillGatingIsConfigurable()
    {
        // The unlock levels are config, not baked: push gene-A back to 5 and a level-1 hero is Strike-only.
        var hero = HeroWithGeneSkill("h", geneAByte: 1, level: 1);
        var strict = GameConfig.Default.Combat with { GeneSkillALevel = 5 };
        var skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level, strict);
        Assert.Single(skills);
        Assert.Equal("strike", skills[0].Id);
    }

    [Fact]
    public void TacticalPolicyCastsBuffsThatGreedyIgnores()
    {
        // War Cry (a Focus buff, Power 35) is strictly weaker than Strike (Power 40), so a pure
        // damage-maximiser never casts it. Tactical opens with it; Greedy never does.
        var buffer = HeroWithGeneSkill("buffer", geneAByte: 9);   // gene-A = War Cry (Focus)
        var foe = MakeHero("foe", 128, level: 5);
        var seed = SHA256.HashData([42]);

        var tactical = BattleEngine.Fight(buffer, foe, seed);     // default policy = Tactical
        Assert.Contains(tactical.Events, e => e.ActorId == "buffer" && e.Effect == SkillEffect.Focus);

        var greedyCfg = GameConfig.Default with
        {
            Combat = GameConfig.Default.Combat with { SelectionPolicy = CombatSelectionPolicy.Greedy }
        };
        var greedy = BattleEngine.Fight(buffer, foe, seed, greedyCfg);
        Assert.DoesNotContain(greedy.Events, e => e.ActorId == "buffer" && e.Effect == SkillEffect.Focus);
    }

    [Fact]
    public void TacticalHeroHealsWhenHurt()
    {
        // A drainer, once hurt past the heal threshold, casts Leech Strike and recovers HP —
        // a drain event with a positive heal. Checked across seeds so it doesn't hinge on one roll.
        var drainer = HeroWithGeneSkill("drainer", geneAByte: 12); // gene-A = Leech Strike (DrainHalf)
        var foe = MakeHero("foe", 150, level: 6);                  // stronger, so the drainer takes real damage
        var healed = false;
        for (var n = 0; n < 30 && !healed; n++)
        {
            var seed = SHA256.HashData(BitConverter.GetBytes(5000 + n));
            var result = BattleEngine.Fight(drainer, foe, seed);
            healed = result.Events.Any(e =>
                e.ActorId == "drainer" && e.Effect == SkillEffect.DrainHalf && e.Healed > 0);
        }
        Assert.True(healed, "A hurt drainer should cast Leech Strike and heal at least once across seeds.");
    }

    [Fact]
    public void TacticalFightWithEffectsIsDeterministic()
    {
        var a = HeroWithGeneSkill("a", geneAByte: 9, level: 7);   // War Cry + gene-B
        var b = HeroWithGeneSkill("b", geneAByte: 12, level: 7);  // Leech Strike + gene-B
        var seed = SHA256.HashData([3, 1, 4]);
        var r1 = BattleEngine.Fight(a, b, seed);
        var r2 = BattleEngine.Fight(a, b, seed);
        Assert.Equal(r1.WinnerId, r2.WinnerId);
        Assert.Equal(r1.Events.Count, r2.Events.Count);
        for (var i = 0; i < r1.Events.Count; i++) Assert.Equal(r1.Events[i], r2.Events[i]);
    }
}
