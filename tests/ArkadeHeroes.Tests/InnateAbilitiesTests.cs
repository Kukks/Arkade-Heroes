using System.Security.Cryptography;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Combat;

namespace ArkadeHeroes.Tests;

public class InnateAbilitiesTests
{
    // A genome expressing given cosmetic traits at given dominant-gene bytes; all else plain.
    // Stat genes are set to a mid value so the hero has usable HP/attack/speed.
    private static Genome GenomeWith(byte statGenes, params (TraitCategory Cat, byte Val)[] traits)
    {
        var b = new byte[32];
        for (var i = 0; i < 8; i++) b[i] = statGenes;          // stat + skill genes
        foreach (var (cat, val) in traits) b[16 + (int)cat * 2] = val;
        return new Genome(b);
    }

    private static Hero HeroWith(string id, int level, Genome genome) =>
        new() { Id = id, OwnerId = "p", Name = id, Genome = genome, Level = level };

    private static GameConfig Innate =>
        GameConfig.Default with { Combat = GameConfig.Default.Combat with { InnateAbilities = true } };

    [Fact]
    public void InnateStrength_ReadsExpressedTierOfOneCategory()
    {
        // Legendary Aura → the Legendary affinity-ladder bonus; plain → 0; an affinity category → 0.
        Assert.Equal(0.030, Traits.InnateStrength(GenomeWith(128, (TraitCategory.Aura, 255)), TraitCategory.Aura), 6);
        Assert.Equal(0.0, Traits.InnateStrength(GenomeWith(128), TraitCategory.Aura), 6);
        Assert.Equal(0.0, Traits.InnateStrength(GenomeWith(128, (TraitCategory.ElementAffinity, 255)), TraitCategory.ElementAffinity), 6);
    }

    [Fact]
    public void InnateBonuses_DefaultIsConservative()
    {
        var ib = InnateBonuses.Default;
        Assert.Equal(3, ib.BrandTurns);
        Assert.True(ib is { Shield: 1.0, Accuracy: 1.0, Thorns: 1.0, Initiative: 1.0, Regen: 0.10, Brand: 0.10 });
        Assert.Same(InnateBonuses.Default, GameConfig.Default.Combat.InnateOrDefault); // null resolves to Default
    }

    [Fact]
    public void Initiative_HigherStanceActsFirstInANearTie()
    {
        // Two heroes with identical stats (same genome stat bytes → identical Speed). Without initiative,
        // TurnOrder falls to the id tiebreak (CompareOrdinal), so "a" (< "b") acts first. Give "b" a Legendary
        // Stance: its effective ordering speed edges ahead, so "b" acts — and lands the first SkillUsed event.
        var plain = GenomeWith(200);                                   // high stats, no traits
        var stanced = GenomeWith(200, (TraitCategory.Stance, 255));    // same stats + Legendary Stance
        var a = HeroWith("a", 20, plain);
        var b = HeroWith("b", 20, stanced);
        var seed = new byte[32]; Array.Fill(seed, (byte)7);

        var firstActor = BattleEngine.Fight(a, b, seed, Innate).Events
            .First(e => e.Kind == BattleEventKind.SkillUsed || e.Kind == BattleEventKind.Missed || e.Kind == BattleEventKind.Dodged)
            .ActorId;
        Assert.Equal("b", firstActor);
    }

    [Fact]
    public void Accuracy_EyesRaisesTheHitThresholdWithoutMovingTheRngStream()
    {
        // Accuracy is threshold-only: DeterministicRng.Chance is Next(100) < clamp(percent), so it draws once
        // regardless of the threshold. Eyes raises the threshold by AccuracyBonus (+3 at Legendary), so a seed
        // whose opening draw lands in [skill.Accuracy, skill.Accuracy + bonus) is a MISS for the plain hero but a
        // HIT for the Eyed hero on the SAME draw. Search deterministically for such a flip seed — its existence
        // proves the bonus moves the compare, not the stream (the identical Next(100) is consumed either way).
        var plain = HeroWith("atk", 20, GenomeWith(140));
        var eyed  = HeroWith("atk", 20, GenomeWith(140, (TraitCategory.Eyes, 255)));
        var def   = HeroWith("def", 20, GenomeWith(140));
        // NOTE: seeds are SHA256-derived (not s[0]=i, s[1]=i>>8). DeterministicRng is xoshiro256** whose FIRST
        // output is a function of the _s1 seed word (bytes 8..15) only; a seed that leaves those bytes zero makes
        // the opening Next(100) draw always 0, so the turn-1 accuracy roll would never miss. A hashed seed varies
        // the opening draw, which is exactly the roll this test needs to land in the flip window.
        byte[] seed = null!;
        for (var i = 0; i < 5000 && seed is null; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var plainFirst = BattleEngine.Fight(plain, def, s, Innate).Events[0];
            var eyedFirst  = BattleEngine.Fight(eyed,  def, s, Innate).Events[0];
            if (plainFirst.Kind == BattleEventKind.Missed && eyedFirst.Kind != BattleEventKind.Missed) seed = s;
        }
        Assert.NotNull(seed);                                                            // the lever is real
        Assert.Equal(BattleEventKind.Missed, BattleEngine.Fight(plain, def, seed, Innate).Events[0].Kind);
        Assert.NotEqual(BattleEventKind.Missed, BattleEngine.Fight(eyed, def, seed, Innate).Events[0].Kind); // Eyes flipped it
    }
}
