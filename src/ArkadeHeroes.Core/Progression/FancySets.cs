using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// "Fancy" heroes — a named title awarded to a hero whose genome expresses a rare COMBINATION of cosmetic
/// traits (the CryptoKitties fancy-cat hook: it gives breeders a concrete target beyond a rarity number).
/// A pure, deterministic, client-verifiable derivation from the immutable genome — like <see cref="Rarity"/>
/// and <see cref="Sterility"/>, it touches no combat, covenant, or receipt. Keyed off the SIX cosmetic trait
/// categories (Aura, Marking, Eyes, Crest, Sigil, Stance); the two affinity traits drive combat, not collection.
/// </summary>
public static class FancySets
{
    /// <summary>The hero's Fancy title, or null if its genome hits no set. The highest-prestige match wins.</summary>
    public static string? TitleFor(Genome genome, GameConfig? config = null)
    {
        var cosmetic = Traits.Expressed(genome).Where(t => !Traits.IsAffinity(t.Category)).ToList();
        bool EpicPlus(TraitCategory c) => Traits.TierOf(genome.DominantGene(c), config) >= RarityTier.Epic;

        // Ultra-grail: three or more cosmetic traits at Legendary.
        if (cosmetic.Count(t => t.Tier >= RarityTier.Legendary) >= 3) return "Sovereign";
        // Themed Epic-or-better pairs.
        if (EpicPlus(TraitCategory.Aura) && EpicPlus(TraitCategory.Sigil)) return "Emberlord";
        if (EpicPlus(TraitCategory.Crest) && EpicPlus(TraitCategory.Stance)) return "Duelist";
        if (EpicPlus(TraitCategory.Eyes) && EpicPlus(TraitCategory.Marking)) return "Oracle";
        // The "full house": every one of the six cosmetic slots is expressed (non-plain).
        if (cosmetic.Count == 6) return "Prismatic";
        return null;
    }
}
