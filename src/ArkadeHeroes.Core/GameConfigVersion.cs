using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Core;

/// <summary>
/// The stable VERSION IDENTIFIER of a <see cref="GameConfig"/> — a domain-tagged SHA-256 over the CANONICAL
/// serialization of its VERIFICATION-CRITICAL block (the first eight members: absorb odds, gene thresholds,
/// fusion threshold, sterility, rarity bands, affinity bonuses, XP curve, combat). That block is exactly the
/// set a deterministic replay consumes, so the id answers the only question a verifier asks: "which RULES was
/// this resolved under?".
///
/// The economy members (fees, pot splits, matchmaking take) are deliberately EXCLUDED. They are
/// <c>GameOptions</c>-tunable at runtime and provably unread by <c>BattleEngine.Fight</c>,
/// <c>Gauntlet/Trials/SquadBattle/Tournament.Resolve</c>, so folding them in would churn the id — and strand
/// every already-stamped replay — on a fee change that cannot alter a single event of a battle log.
///
/// Determinism on BOTH sides of the wire is the whole point, so the encoding takes no chances:
/// integers and ticks render <see cref="CultureInfo.InvariantCulture"/> (digits only, no locale digit shapes
/// or separators), booleans render 1/0, the one enum renders its member NAME (an ASCII identifier, not an
/// ordinal that a later reorder would silently reuse), and every double is written as its EXACT IEEE-754 bit
/// pattern in explicit LITTLE-ENDIAN byte order — never as formatted text, so no rounding, shortest-roundtrip
/// or locale decimal-separator question can arise, and a big-endian host computes the identical bytes. The
/// hash then reads UTF-8. Server, x64 client, and WASM client therefore agree byte-for-byte in any locale on
/// any architecture. (Same discipline as <c>FairnessAudit.ComputeEntrantsCommitment</c>'s invariant canonical
/// form and the tournament shuffle's <see cref="BinaryPrimitives"/> little-endian reads.)
///
/// A null <c>Combat.Innate</c> resolves through <see cref="CombatConfig.InnateOrDefault"/> before hashing, so
/// the two spellings of the same rules — omitted knobs vs. explicit <see cref="InnateBonuses.Default"/> —
/// share one id, exactly as they share one engine behaviour.
/// </summary>
public static class GameConfigVersion
{
    /// <summary>The domain tag pinning this canonical serialization's version. Bump it if the encoding changes.</summary>
    private const string Tag = "arkade-gameconfig-v1";

    /// <summary>The id of <see cref="GameConfig.Default"/> — the rules every UNSTAMPED (pre-stamp) replay was
    /// resolved under, and the one id a client can always resolve offline from its compiled-in constant.</summary>
    public static string Default { get; } = Compute(GameConfig.Default);

    /// <summary>The version id of <paramref name="config"/>: 64 lowercase hex chars.</summary>
    public static string Compute(GameConfig config)
    {
        var canon = new StringBuilder(Tag);
        Append(canon, config);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString()))).ToLowerInvariant();
    }

    private static void Append(StringBuilder c, GameConfig g)
    {
        var a = g.Absorb;
        c.Append("\nabsorb").Append('|').Append(Num(a.AbsorbChance)).Append('|').Append(Num(a.ContinueChance));

        var gene = g.Gene;
        c.Append("\ngene").Append('|').Append(Num(gene.RegionMutationThreshold))
         .Append('|').Append(Num(gene.TraitMutationThreshold));

        c.Append("\nfusion").Append('|').Append(Num(g.FusionConcentrateThreshold));

        var s = g.Sterility;
        c.Append("\nsterility").Append('|').Append(Num(s.Legendary)).Append('|').Append(Num(s.Epic))
         .Append('|').Append(Num(s.Rare)).Append('|').Append(Num(s.Uncommon));

        var r = g.Rarity;
        c.Append("\nrarity").Append('|').Append(Num(r.LegendaryCutoff)).Append('|').Append(Num(r.EpicCutoff))
         .Append('|').Append(Num(r.RareCutoff)).Append('|').Append(Num(r.UncommonCutoff))
         .Append('|').Append(Num(r.LegendaryWeight)).Append('|').Append(Num(r.EpicWeight))
         .Append('|').Append(Num(r.RareWeight)).Append('|').Append(Num(r.UncommonWeight))
         .Append('|').Append(Num(r.CommonWeight));

        var af = g.Affinity;
        c.Append("\naffinity").Append('|').Append(Bits(af.Legendary)).Append('|').Append(Bits(af.Epic))
         .Append('|').Append(Bits(af.Rare)).Append('|').Append(Bits(af.Uncommon))
         .Append('|').Append(Bits(af.Common)).Append('|').Append(Bits(af.Cap));

        var x = g.Curve;
        c.Append("\ncurve").Append('|').Append(Num(x.Base)).Append('|').Append(Bits(x.Coefficient))
         .Append('|').Append(Bits(x.Exponent)).Append('|').Append(Num(x.MaxLevel));

        var m = g.Combat;
        c.Append("\ncombat").Append('|').Append(Num(m.MaxTurns)).Append('|').Append(Bits(m.ElementStrong))
         .Append('|').Append(Bits(m.ElementWeak)).Append('|').Append(Bits(m.CritMultiplier))
         .Append('|').Append(Bits(m.ArmorConstant))
         .Append('|').Append(Num(m.GeneSkillALevel)).Append('|').Append(Num(m.GeneSkillBLevel))
         .Append('|').Append(Num(m.BurstLevel))
         .Append('|').Append(Bits(m.FocusPerStack)).Append('|').Append(Bits(m.DefenseBreakPerStack))
         .Append('|').Append(Num(m.MaxEffectStacks)).Append('|').Append(Bits(m.DrainFraction))
         .Append('|').Append(m.SelectionPolicy.ToString())
         .Append('|').Append(Num(m.HealHpThresholdPercent))
         .Append('|').Append(Flag(m.ElementAwareSelection)).Append('|').Append(Flag(m.InnateAbilities))
         .Append('|').Append(Flag(m.SquadSynergy)).Append('|').Append(Flag(m.GearCounters));

        // Innate: null and an explicit InnateBonuses.Default are the SAME rules (InnateOrDefault), so they
        // must hash the same — resolve before appending.
        var i = m.InnateOrDefault;
        c.Append("\ninnate").Append('|').Append(Bits(i.ShieldChance)).Append('|').Append(Bits(i.Ward))
         .Append('|').Append(Bits(i.RegenChance)).Append('|').Append(Bits(i.Mend))
         .Append('|').Append(Bits(i.TrueStrikeChance))
         .Append('|').Append(Bits(i.ThornsChance)).Append('|').Append(Bits(i.Reflect))
         .Append('|').Append(Bits(i.BrandChance)).Append('|').Append(Bits(i.Tick))
         .Append('|').Append(Num(i.BrandTurns)).Append('|').Append(Bits(i.InitiativeChance));

        // Counters: same null-resolution rule as Innate — an omitted record and an explicit
        // GearCounterRules.Default are one behaviour (CountersOrDefault), so they must be one id.
        var gc = m.CountersOrDefault;
        c.Append("\ncounters").Append('|').Append(Bits(gc.Edge)).Append('|').Append(Bits(gc.OffenseShare))
         .Append('|').Append(Bits(gc.BulkShare)).Append('|').Append(Bits(gc.TempoShare));
    }

    /// <summary>An integral value as invariant digits — no locale digit shapes, signs, or group separators.</summary>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A boolean as 1/0 — no culture, no casing question.</summary>
    private static string Flag(bool value) => value ? "1" : "0";

    /// <summary>
    /// A double as its EXACT IEEE-754 bits in explicit little-endian order. Hashing the bit pattern rather
    /// than formatted text removes every formatting variable at once — rounding, shortest-roundtrip choice,
    /// and the decimal separator — and the explicit endianness makes a big-endian host produce identical bytes.
    /// </summary>
    private static string Bits(double value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, BitConverter.DoubleToUInt64Bits(value));
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
