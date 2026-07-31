using System.Security.Cryptography;

namespace ArkadeHeroes.Core.Genetics;

/// <summary>
/// A hero's immutable 32-byte genome. Mirrors the ArkadeKitties layout philosophy:
/// the genome is committed on-chain in the asset's genesis metadata and every
/// visible trait is <em>derived</em> from it off-chain — nothing else is stored.
///
/// Byte map (Arkade Heroes trait map v1):
///   [0]  Strength gene        [1]  Vitality gene       [2]  Agility gene
///   [3]  Intellect gene       [4]  Luck gene
///   [5]  Element gene (mod 8)
///   [6]  Skill gene A         [7]  Skill gene B
///   [8..12] Growth genes for STR/VIT/AGI/INT/LUK (hidden potential — the
///           breeding meta: invisible in base stats, dominant at high level)
///   [13] Cooldown gene (breeding recovery multiplier)
///   [14..15] Appearance genes (console flavor: title + palette)
///   [16..31] Reserved (zero in v1; future trait-map versions)
/// </summary>
public readonly struct Genome : IEquatable<Genome>
{
    public const int Size = 32;

    private readonly byte[] _bytes;

    public Genome(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Genome must be exactly {Size} bytes, got {bytes.Length}.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes ?? throw new InvalidOperationException("Uninitialized genome.");

    public byte this[int index] => Bytes[index];

    // Stat genes
    public byte StrengthGene => Bytes[0];
    public byte VitalityGene => Bytes[1];
    public byte AgilityGene => Bytes[2];
    public byte IntellectGene => Bytes[3];
    public byte LuckGene => Bytes[4];

    public Element Element => (Element)(Bytes[5] % 8);

    public byte SkillGeneA => Bytes[6];
    public byte SkillGeneB => Bytes[7];

    public byte GrowthGene(Stat stat) => Bytes[8 + (int)stat];

    public byte CooldownGene => Bytes[13];

    public byte AppearanceTitleGene => Bytes[14];
    public byte AppearancePaletteGene => Bytes[15];

    /// <summary>Base offset of the trait-category block in the reserved region.</summary>
    private const int TraitBase = 16;

    /// <summary>The EXPRESSED (visible) gene of a trait category — byte 16 + c*2.</summary>
    public byte DominantGene(TraitCategory category) => Bytes[TraitBase + (int)category * 2];

    /// <summary>The HIDDEN (recessive) gene of a trait category — byte 16 + c*2 + 1.</summary>
    public byte RecessiveGene(TraitCategory category) => Bytes[TraitBase + (int)category * 2 + 1];

    public string ToHex() => Convert.ToHexString(Bytes).ToLowerInvariant();

    public static Genome FromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        return new Genome(Convert.FromHexString(hex));
    }

    /// <summary>
    /// Derives a generation-0 genome deterministically from 32 bytes of entropy.
    /// Reserved bytes [16..31] are zeroed so v1 genomes are forward-compatible
    /// with later trait-map versions.
    /// </summary>
    public static Genome NewGen0(ReadOnlySpan<byte> entropy)
    {
        Span<byte> genes = stackalloc byte[Size];
        SHA256.HashData(entropy, genes);
        genes[16..].Clear();
        return new Genome(genes);
    }

    /// <summary>
    /// A bought hero: <see cref="NewGen0"/> with its stat and growth genes squashed into the bottom of
    /// their range.
    ///
    /// <para>Recruits can be bought over and over, so their genome cannot be a lottery ticket. Gen-0 is
    /// already trait-blank — bytes [16..] are cleared, so a recruit expresses nothing and scores zero
    /// rarity — but stats come from raw hash bytes, and an unlimited supply of those is an unlimited number
    /// of rolls at a good statline. Capping them makes a recruit reliably the worst hero available, which
    /// is what keeps bred heroes worth breeding.</para>
    ///
    /// <para>Still a pure function of the entropy, so anyone holding the seed can recompute the genome and
    /// check the server minted what it said it did.</para>
    /// </summary>
    public static Genome NewRecruit(ReadOnlySpan<byte> entropy, byte statCap)
    {
        Span<byte> genes = stackalloc byte[Size];
        SHA256.HashData(entropy, genes);
        genes[16..].Clear();

        // Modulo rather than clamp: clamping would pile every high roll onto the cap exactly, making the
        // ceiling the single most common value. This keeps the low band evenly covered.
        var span = statCap + 1;
        for (var i = 0; i <= 4; i++) genes[i] = (byte)(genes[i] % span);            // Might..Fortune
        for (var i = 8; i <= 12; i++) genes[i] = (byte)(genes[i] % span);           // per-stat growth
        return new Genome(genes);
    }

    public bool Equals(Genome other) => Bytes.SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is Genome g && Equals(g);
    public override int GetHashCode() => BitConverter.ToInt32(Bytes[..4]);
    public static bool operator ==(Genome left, Genome right) => left.Equals(right);
    public static bool operator !=(Genome left, Genome right) => !left.Equals(right);
    public override string ToString() => ToHex();
}

/// <summary>The five primary stats a hero derives from its genome.</summary>
public enum Stat
{
    Strength = 0,
    Vitality = 1,
    Agility = 2,
    Intellect = 3,
    Luck = 4,
}

/// <summary>Eight-element ring: each element is strong against the next and weak against the previous.</summary>
public enum Element
{
    Ember = 0,
    Gale = 1,
    Terra = 2,
    Tide = 3,
    Volt = 4,
    Frost = 5,
    Radiant = 6,
    Umbral = 7,
}
