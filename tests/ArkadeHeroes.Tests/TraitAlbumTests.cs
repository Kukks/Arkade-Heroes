using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>The trait album counts distinct rarity tiers covered per cosmetic category across a roster — a pure,
/// genome-derived Pokédex over the six cosmetic categories (affinity traits are excluded).</summary>
public class TraitAlbumTests
{
    [Fact]
    public void Coverage_CountsDistinctTiersPerCosmeticCategory_ExcludingAffinity()
    {
        // A hero with a Legendary Aura (dominant gene 255) covers Aura at one tier; nothing else is expressed.
        var g = new byte[32];
        g[16 + (int)TraitCategory.Aura * 2] = 255;
        var cov = TraitAlbum.CoverageByCategory(new[] { new Genome(g) });

        Assert.Equal(6, cov.Count);                                // six cosmetic categories, always present
        Assert.Equal(1, cov[TraitCategory.Aura]);                  // one tier (Legendary) collected
        Assert.Equal(0, cov[TraitCategory.Marking]);               // plain slot → nothing collected
        Assert.DoesNotContain(TraitCategory.ElementAffinity, cov.Keys);   // affinity drives combat, not collection
    }
}
