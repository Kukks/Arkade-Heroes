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

    /// <summary>Maps a gene byte to its rarity tier. Value 0 ("plain") reads as the Common floor. Cutoffs from config.</summary>
    public static RarityTier TierOf(byte value, GameConfig? config = null)
    {
        var b = (config ?? GameConfig.Default).Rarity;
        if (value >= b.LegendaryCutoff) return RarityTier.Legendary;   // 255        (~0.4%)
        if (value >= b.EpicCutoff) return RarityTier.Epic;             // 253..254   (~0.8%)
        if (value >= b.RareCutoff) return RarityTier.Rare;             // 241..252   (~4.7%)
        if (value >= b.UncommonCutoff) return RarityTier.Uncommon;     // 206..240   (~13.7%)
        return RarityTier.Common;                                      // 0..205
    }

    /// <summary>Rarity weight of a gene value — used to score a hero. Common plain (0) weighs 0.</summary>
    public static int WeightOf(byte value, GameConfig? config = null)
    {
        if (value == 0) return 0;
        var w = (config ?? GameConfig.Default).Rarity;
        return TierOf(value, config) switch
        {
            RarityTier.Legendary => w.LegendaryWeight,
            RarityTier.Epic => w.EpicWeight,
            RarityTier.Rare => w.RareWeight,
            RarityTier.Uncommon => w.UncommonWeight,
            _ => w.CommonWeight,
        };
    }

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

    /// <summary>
    /// The capped combat multiplier from a hero's EXPRESSED AFFINITY traits (1.0..1.05).
    /// Each affinity tier adds a small share; the sum is clamped to the cap so a
    /// max-rolled hero is a nudge, never a trump card. Deterministic — pure genome function.
    /// </summary>
    public static double AffinityModifier(Genome genome, GameConfig? config = null)
    {
        var a = (config ?? GameConfig.Default).Affinity;
        double bonus = 0;
        foreach (var trait in Expressed(genome))
        {
            if (!IsAffinity(trait.Category)) continue;
            bonus += TierOf(trait.Value, config) switch
            {
                RarityTier.Legendary => a.Legendary,
                RarityTier.Epic => a.Epic,
                RarityTier.Rare => a.Rare,
                RarityTier.Uncommon => a.Uncommon,
                _ => a.Common,
            };
        }
        return 1.0 + Math.Min(bonus, a.Cap);
    }

    /// <summary>
    /// The capped combat STRENGTH of a hero's EXPRESSED (dominant) trait in ONE cosmetic category — the shared
    /// 0..<see cref="AffinityBonuses.Cap"/> magnitude each innate-v2 passive spends in its own units. Same
    /// per-tier ladder as <see cref="AffinityModifier"/>; 0 if the gene is plain (value 0) or the category is an
    /// affinity. Deterministic — a pure function of the immutable genome.
    /// </summary>
    public static double InnateStrength(Genome genome, TraitCategory category, GameConfig? config = null)
    {
        if (IsAffinity(category)) return 0;
        var value = genome.DominantGene(category);
        if (value == 0) return 0;
        var a = (config ?? GameConfig.Default).Affinity;
        var bonus = TierOf(value, config) switch
        {
            RarityTier.Legendary => a.Legendary,
            RarityTier.Epic => a.Epic,
            RarityTier.Rare => a.Rare,
            RarityTier.Uncommon => a.Uncommon,
            _ => a.Common,
        };
        return Math.Min(bonus, a.Cap);
    }

    /// <summary>
    /// The innate-v2 combat passives a hero actually GRANTS: for each of the six COSMETIC categories whose
    /// expressed (dominant) trait carries non-zero <see cref="InnateStrength"/>, its category + rarity tier.
    /// Affinity categories never appear (they grant no innate passive), and a plain gene (value 0, strength 0)
    /// is omitted. The category→passive naming (Aura→Shield, Marking→Regen, Eyes→Accuracy, Crest→Thorns,
    /// Sigil→Brand, Stance→Initiative) is a display concern left to the caller. Deterministic — a pure function
    /// of the immutable genome; the frontend surfaces these only when <see cref="CombatConfig.InnateAbilities"/>
    /// is published on (default off).
    /// </summary>
    public static IReadOnlyList<(TraitCategory Category, RarityTier Tier)> InnatePassives(Genome genome, GameConfig? config = null)
    {
        var list = new List<(TraitCategory, RarityTier)>();
        for (var c = 0; c < CategoryCount; c++)
        {
            var cat = (TraitCategory)c;
            if (InnateStrength(genome, cat, config) <= 0) continue;   // skips affinities and plain (0) genes
            list.Add((cat, TierOf(genome.DominantGene(cat), config)));
        }
        return list;
    }
}
