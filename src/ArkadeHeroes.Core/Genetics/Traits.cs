namespace ArkadeHeroes.Core.Genetics;

/// <summary>The 8 heritable trait categories packed into genome bytes [16..31] (2 bytes each: dominant + recessive). The last two are affinities.</summary>
public enum TraitCategory
{
    Aura = 0, Marking = 1, Eyes = 2, Crest = 3, Sigil = 4, Stance = 5,
    ElementAffinity = 6, Temperament = 7,
}

/// <summary>Rarity tiers, from a trait variant's gene value.</summary>
public enum RarityTier { Common = 0, Uncommon = 1, Rare = 2, Epic = 3, Legendary = 4 }

/// <summary>An expressed or carried trait: its category, gene value, and tier.</summary>
public readonly record struct TraitVariant(TraitCategory Category, byte Value, RarityTier Tier);

/// <summary>Trait derivation from a genome — categories, rarity bands, and the capped combat affinity. Pure; every result is a function of the immutable genome.</summary>
public static class Traits
{
    public const int CategoryCount = 8;

    /// <summary>The two affinity categories whose expressed variant grants a capped combat nudge.</summary>
    public static bool IsAffinity(TraitCategory category)
        => category is TraitCategory.ElementAffinity or TraitCategory.Temperament;

    /// <summary>Maps a gene byte to its rarity tier. Value 0 ("plain") reads as the Common floor.</summary>
    public static RarityTier TierOf(byte value) => value switch
    {
        >= 255 => RarityTier.Legendary,   // 255        (~0.4%)
        >= 253 => RarityTier.Epic,         // 253..254   (~0.8%)
        >= 241 => RarityTier.Rare,         // 241..252   (~4.7%)
        >= 206 => RarityTier.Uncommon,     // 206..240   (~13.7%)
        _ => RarityTier.Common,            // 0..205
    };

    /// <summary>Rarity weight of a gene value — used to score a hero. Common plain (0) weighs 0.</summary>
    public static int WeightOf(byte value) => value == 0 ? 0 : TierOf(value) switch
    {
        RarityTier.Legendary => 50,
        RarityTier.Epic => 20,
        RarityTier.Rare => 8,
        RarityTier.Uncommon => 3,
        _ => 1,
    };

    /// <summary>The hero's EXPRESSED (visible) traits — non-zero dominant genes.</summary>
    public static IReadOnlyList<TraitVariant> Expressed(Genome genome) => Collect(genome, dominant: true);

    /// <summary>The hero's CARRIED recessives — non-zero recessive genes (hidden breeding potential).</summary>
    public static IReadOnlyList<TraitVariant> Recessives(Genome genome) => Collect(genome, dominant: false);

    private static List<TraitVariant> Collect(Genome genome, bool dominant)
    {
        var list = new List<TraitVariant>();
        for (var c = 0; c < CategoryCount; c++)
        {
            var cat = (TraitCategory)c;
            var value = dominant ? genome.DominantGene(cat) : genome.RecessiveGene(cat);
            if (value != 0) list.Add(new TraitVariant(cat, value, TierOf(value)));
        }
        return list;
    }
}
