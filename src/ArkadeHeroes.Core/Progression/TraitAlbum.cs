using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// A "trait album" — Pokédex-style collection coverage over the SIX cosmetic trait categories × the FIVE rarity
/// tiers (30 cells). A cell is covered when the player's roster expresses that category at that tier. Pure and
/// genome-derived (client-verifiable); the two affinity traits drive combat, not collection, so they're excluded.
/// </summary>
public static class TraitAlbum
{
    /// <summary>The six cosmetic categories (everything but the two affinity traits), in genome order.</summary>
    public static readonly IReadOnlyList<TraitCategory> Categories =
        Enum.GetValues<TraitCategory>().Where(c => !Traits.IsAffinity(c)).ToList();

    /// <summary>The number of rarity tiers a category can be collected at (Common → Legendary).</summary>
    public const int TiersPerCategory = 5;

    /// <summary>Distinct tiers covered per cosmetic category across the genomes — every category is present (0 if none).</summary>
    public static IReadOnlyDictionary<TraitCategory, int> CoverageByCategory(IEnumerable<Genome> genomes)
    {
        var seen = Categories.ToDictionary(c => c, _ => new HashSet<RarityTier>());
        foreach (var g in genomes)
            foreach (var t in Traits.Expressed(g))
                if (!Traits.IsAffinity(t.Category))
                    seen[t.Category].Add(t.Tier);
        return Categories.ToDictionary(c => c, c => seen[c].Count);
    }
}
