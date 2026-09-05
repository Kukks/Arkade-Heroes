using System.Security.Cryptography;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Heroes;

/// <summary>Derives a display name from the genome — the console client's stand-in for art.</summary>
public static class HeroNamer
{
    private static readonly string[] Palettes =
    [
        "Crimson", "Azure", "Verdant", "Golden", "Obsidian", "Ivory", "Violet", "Copper",
        "Cerulean", "Scarlet", "Jade", "Amber", "Onyx", "Pearl", "Indigo", "Rust",
        "Ashen", "Cobalt", "Sable", "Russet", "Saffron", "Teal", "Garnet", "Slate",
        "Bronze", "Pewter", "Magenta", "Olive", "Cinder", "Opal", "Maroon", "Silver",
    ];

    private static readonly string[] Titles =
    [
        "Vanguard", "Warden", "Trickster", "Sage", "Reaver", "Sentinel", "Nomad", "Oracle",
        "Duelist", "Harbinger", "Keeper", "Stalker", "Templar", "Wanderer", "Channeler", "Marauder",
        "Bulwark", "Seeker", "Ravager", "Herald", "Paragon", "Lancer", "Warlock", "Ranger",
        "Zealot", "Adept", "Corsair", "Falconer", "Sunderer", "Arbiter", "Prowler", "Beacon",
    ];

    /// <summary>House-marks, kept to four characters so the longest derivable name still fits
    /// <see cref="Progression.NameRegistry.MaxLength"/> — a generated name must be one a player could
    /// legally have claimed.</summary>
    private static readonly string[] Marks =
    [
        "Vale", "Kesh", "Orin", "Bryn", "Thal", "Mire", "Dusk", "Fenn",
        "Hale", "Rook", "Vexx", "Ardh", "Sarn", "Grim", "Nyx", "Pike",
        "Wren", "Cove", "Bane", "Loch", "Fell", "Mote", "Skal", "Rune",
        "Tor", "Vosk", "Drem", "Ilex", "Quen", "Bral", "Xan", "Corr",
    ];

    /// <summary>The domain tag separating the mark's hash from every other genome hash in the game.</summary>
    private static readonly byte[] MarkTag = "arkade-hero-mark-v1"u8.ToArray();

    /// <summary>The longest name <see cref="DeriveName"/> can produce, computed from the word lists rather
    /// than sampled, so a word too long to be a claimable name fails a test instead of a player.</summary>
    public static readonly int MaxDerivedNameLength =
        Palettes.Max(w => w.Length) + Titles.Max(w => w.Length) + Marks.Max(w => w.Length) + 2;

    public static string DeriveName(Genome genome)
    {
        var palette = Palettes[genome.AppearancePaletteGene % Palettes.Length];
        var title = Titles[genome.AppearanceTitleGene % Titles.Length];
        return $"{palette} {title} {Marks[MarkIndex(genome)]}";
    }

    /// <summary>
    /// The mark is drawn from a hash of the WHOLE genome, deliberately unlike the palette and title, which
    /// read single appearance bytes. Those bytes are crossover regions, so a child inherits both from one
    /// parent — and therefore that parent's exact name — about half the time. A child's genome always
    /// differs from its parents', so a whole-genome hash cannot be inherited wholesale: siblings and
    /// parent-child pairs keep the family's palette and title while separating on the mark.
    /// </summary>
    private static int MarkIndex(Genome genome)
    {
        Span<byte> preimage = stackalloc byte[MarkTag.Length + Genome.Size];
        MarkTag.CopyTo(preimage);
        genome.Bytes.CopyTo(preimage[MarkTag.Length..]);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(preimage, digest);
        return digest[0] % Marks.Length;
    }
}
