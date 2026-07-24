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
}
