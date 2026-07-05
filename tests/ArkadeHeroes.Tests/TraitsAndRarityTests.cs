using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

public class TraitsAndRarityTests
{
    // Builds a genome with a single trait: dominant byte for `cat` set to `value`.
    private static Genome GenomeWithTrait(TraitCategory cat, byte value)
    {
        var bytes = new byte[32];
        bytes[16 + (int)cat * 2] = value; // dominant gene of the category
        return new Genome(bytes);
    }

    [Fact]
    public void TierBandsMapByteValueToTiers()
    {
        Assert.Equal(RarityTier.Common, Traits.TierOf(0));      // none/plain reads as Common floor
        Assert.Equal(RarityTier.Common, Traits.TierOf(100));
        Assert.Equal(RarityTier.Uncommon, Traits.TierOf(220));
        Assert.Equal(RarityTier.Rare, Traits.TierOf(245));
        Assert.Equal(RarityTier.Epic, Traits.TierOf(253));
        Assert.Equal(RarityTier.Legendary, Traits.TierOf(255));
    }

    [Fact]
    public void ExpressedReadsDominantNonZeroTraits()
    {
        var g = GenomeWithTrait(TraitCategory.Aura, 255);
        var expressed = Traits.Expressed(g);
        Assert.Single(expressed);
        Assert.Equal(TraitCategory.Aura, expressed[0].Category);
        Assert.Equal(RarityTier.Legendary, expressed[0].Tier);
    }

    [Fact]
    public void AffinityCategoriesAreFlagged()
    {
        Assert.True(Traits.IsAffinity(TraitCategory.ElementAffinity));
        Assert.True(Traits.IsAffinity(TraitCategory.Temperament));
        Assert.False(Traits.IsAffinity(TraitCategory.Aura));
    }
}
