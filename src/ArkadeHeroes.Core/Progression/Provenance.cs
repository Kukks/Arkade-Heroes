namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// Lineage descriptors derived purely from a hero's generation — a breeding-depth "pedigree" ladder that
/// complements the generation-0 Founder status. Deterministic and client-verifiable, like <see cref="Rarity"/>
/// and <see cref="FancySets"/>; touches no combat, covenant, or receipt.
/// </summary>
public static class Provenance
{
    /// <summary>The hero's pedigree title from its breeding depth, or null for a generation-0 original (a "Founder").</summary>
    public static string? Pedigree(int generation) => generation switch
    {
        <= 0 => null,        // gen-0 originals are Founders (surfaced separately)
        <= 2 => "Scion",     // gen 1–2
        <= 4 => "Heir",      // gen 3–4
        _ => "Dynasty",      // gen 5+
    };
}
