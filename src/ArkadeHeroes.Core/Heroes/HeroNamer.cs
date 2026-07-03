using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Heroes;

/// <summary>Derives a display name from the appearance genes — the console client's stand-in for art.</summary>
public static class HeroNamer
{
    private static readonly string[] Palettes =
    [
        "Crimson", "Azure", "Verdant", "Golden", "Obsidian", "Ivory", "Violet", "Copper",
        "Cerulean", "Scarlet", "Jade", "Amber", "Onyx", "Pearl", "Indigo", "Rust",
    ];

    private static readonly string[] Titles =
    [
        "Vanguard", "Warden", "Trickster", "Sage", "Reaver", "Sentinel", "Nomad", "Oracle",
        "Duelist", "Harbinger", "Keeper", "Stalker", "Templar", "Wanderer", "Channeler", "Marauder",
    ];

    public static string DeriveName(Genome genome)
    {
        var palette = Palettes[genome.AppearancePaletteGene % Palettes.Length];
        var title = Titles[genome.AppearanceTitleGene % Titles.Length];
        return $"{palette} {title}";
    }
}
