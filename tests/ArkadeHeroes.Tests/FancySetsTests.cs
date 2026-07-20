using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;   // DtoMapper.ToDto

namespace ArkadeHeroes.Tests;

/// <summary>Fancy titles are a pure Genome→string? derivation over the six COSMETIC trait categories —
/// deterministic + client-verifiable, like Rarity/Sterility (no combat/covenant/receipt).</summary>
public class FancySetsTests
{
    static Genome With(params (TraitCategory Cat, byte Val)[] traits)
    {
        var b = new byte[32];
        foreach (var (cat, val) in traits) b[16 + (int)cat * 2] = val;   // set each dominant gene
        return new Genome(b);
    }

    [Fact]
    public void Blank_HasNoFancy() => Assert.Null(FancySets.TitleFor(new Genome(new byte[32])));

    [Fact]
    public void ThreeLegendaryCosmetics_IsSovereign() =>
        Assert.Equal("Sovereign", FancySets.TitleFor(With(
            (TraitCategory.Aura, 255), (TraitCategory.Eyes, 255), (TraitCategory.Crest, 255))));

    [Fact]
    public void AuraAndSigilEpic_IsEmberlord() =>
        Assert.Equal("Emberlord", FancySets.TitleFor(With(
            (TraitCategory.Aura, 253), (TraitCategory.Sigil, 253))));   // both Epic tier

    [Fact]
    public void AllSixCosmeticsExpressed_IsPrismatic() =>
        Assert.Equal("Prismatic", FancySets.TitleFor(With(
            (TraitCategory.Aura, 100), (TraitCategory.Marking, 100), (TraitCategory.Eyes, 100),
            (TraitCategory.Crest, 100), (TraitCategory.Sigil, 100), (TraitCategory.Stance, 100))));

    [Fact]
    public void AffinityTraitsDoNotCount() =>   // two Legendary AFFINITY traits are not cosmetic → no Fancy
        Assert.Null(FancySets.TitleFor(With(
            (TraitCategory.ElementAffinity, 255), (TraitCategory.Temperament, 255))));

    [Fact]
    public void ToDto_PopulatesFancyTitle_FromTheGenome()
    {
        var hero = new Hero
        {
            Id = "h", OwnerId = "o", Name = "H", Level = 1,
            Genome = With((TraitCategory.Aura, 255), (TraitCategory.Eyes, 255), (TraitCategory.Crest, 255)),
        };
        Assert.Equal("Sovereign", hero.ToDto().FancyTitle);
    }
}
