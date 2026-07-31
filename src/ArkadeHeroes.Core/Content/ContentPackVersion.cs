using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Core.Content;

/// <summary>
/// The stable VERSION IDENTIFIER of a <see cref="ContentPack"/> — a domain-tagged SHA-256 over the
/// CANONICAL serialization of every authored value a deterministic replay can read.
///
/// It exists for the same reason <see cref="GameConfigVersion"/> does, and it is deliberately a SEPARATE
/// id rather than a new block folded into that one. Folding content into the config id would change
/// <see cref="GameConfigVersion.Default"/>, and every already-stamped replay in the wild names that
/// constant — they would all 404 at <c>GET /api/config/{version}</c> and stop verifying. A second,
/// independent stamp adds information without invalidating a single artifact: an outcome that carries no
/// content stamp was resolved before content was authored, i.e. under the gear the binary compiled in,
/// which is a FACT about those artifacts rather than a fallback.
///
/// Determinism on both sides of the wire is the whole point, so the encoding takes no chances, and it
/// reuses <see cref="GameConfigVersion"/>'s discipline verbatim:
///   * integers render <see cref="CultureInfo.InvariantCulture"/> — digits only, no locale digit shapes,
///     no group separators, no locale-specific minus sign (Arabic-Indic digits and the tr-TR casing rules
///     are the bugs this repo has actually been bitten by);
///   * booleans render 1/0, so no casing question arises;
///   * enums render their member NAME, an ASCII identifier — never the ordinal, which a later reorder
///     would silently reuse and which would quietly re-point every drop table;
///   * the hash then reads UTF-8, and the id is lowercase hex.
///
/// There are NO floating-point values in the v1 content schema, by design: a drop chance is an integer
/// WEIGHT, so drop selection is pure integer arithmetic and the endianness question that
/// <see cref="Bits"/> exists to answer cannot arise on the paying path. <see cref="Bits"/> is kept, and
/// <c>ContentPackVersionTests.TheContentSchemaAdmitsNoFloatingPointValue</c> fails the build if a double
/// or float is ever added to the pack's shape without being written through it.
///
/// Every authored value is included — there is no "cosmetic" exclusion here, unlike the config id's
/// deliberate omission of the economy. A NAME is part of the id because a client shows the player what
/// they are holding, and an id that let two packs disagree about an item's name would let a server
/// relabel someone's inventory without changing the stamp.
/// </summary>
public static class ContentPackVersion
{
    /// <summary>The domain tag pinning this canonical serialization's version. Bump it if the encoding
    /// changes. Distinct from the config tag so the two id spaces can never collide.</summary>
    private const string Tag = "arkade-contentpack-v1";

    /// <summary>The id of <see cref="ContentPack.Default"/> — the content this build compiled in, and the
    /// one id a client can always resolve offline from its own embedded pack.
    ///
    /// COMPUTED ON FIRST READ, and deliberately not from a static field initializer. An initializer here
    /// gives this type a static constructor, and <see cref="ContentPack"/>'s own static constructor reaches
    /// this type on its way through <see cref="ContentValidation.Seal"/> → <see cref="ItemCanon"/>. That is
    /// a cycle: our constructor would read <see cref="ContentPack.Default"/> while the initializer that
    /// assigns it is still running, get null, and throw — taking down every type that touches the content
    /// pack, <c>Gauntlet</c> and <c>Trials</c> included.
    ///
    /// Whether the cycle FIRES is a property of the runtime, which is why it shipped: with an initializer
    /// the type is <c>beforefieldinit</c>, and that only promises the constructor runs before the first
    /// static FIELD access. CoreCLR defers it past the <see cref="ItemCanon"/> call, so the server and the
    /// whole test suite never see it. Mono — what Blazor WebAssembly runs — does not, so it fired in the
    /// browser and nowhere else. Reading the value lazily removes the constructor, so neither order can
    /// cycle: reaching <see cref="ItemCanon"/> now initializes nothing at all, and asking for
    /// <see cref="Default"/> first simply completes the pack's initializer before hashing it.
    ///
    /// A torn read costs at worst a second identical hash — <see cref="Compute"/> is pure and
    /// deterministic — so no lock is needed for a value that cannot differ between racers.</summary>
    public static string Default => _default ??= Compute(ContentPack.Default);

    private static string? _default;

    /// <summary>The version id of <paramref name="pack"/>: 64 lowercase hex chars.</summary>
    public static string Compute(ContentPack pack)
    {
        var canon = new StringBuilder(Tag);
        canon.Append("\npack").Append('|').Append(pack.PackId);

        // AUTHORED ORDER is part of the id on purpose. Order is not cosmetic: a drop table's weights are
        // walked in order, so reordering a pack's items or a dungeon's drops can change which item an
        // entropy value selects. Hashing the order means such a reorder is a NEW version, not a silent
        // re-point of an existing one.
        foreach (var i in pack.Items)
            canon.Append("\nitem").Append('|').Append(ItemCanon(i));

        foreach (var d in pack.Dungeons)
        {
            canon.Append("\ndungeon").Append('|').Append(d.Id).Append('|').Append(d.Name)
                 .Append('|').Append(Num(d.EntryFeeBonusSats)).Append('|').Append(Num(d.XpLevelCap))
                 .Append('|').Append(Flag(d.DropRequiresFullClear))
                 .Append('|').Append(d.Roll.ToString());

            foreach (var w in d.Waves)
            {
                canon.Append("\nwave").Append('|').Append(Num(w.Wave)).Append('|').Append(Num(w.LevelOffset))
                     .Append('|').Append(Num(w.Xp));
                foreach (var gear in w.GhostGear) canon.Append('|').Append(gear);
            }

            foreach (var drop in d.Drops)
                canon.Append("\ndrop").Append('|').Append(drop.ItemId).Append('|').Append(Num(drop.Weight));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// EVERY field of one item, canonically. Shared by <see cref="Compute"/> and by the add-only seal in
    /// <see cref="ContentValidation"/> deliberately: if the two spelled an item out separately they could
    /// drift, and the seal would then stop covering exactly the bytes the version id covers.
    /// </summary>
    internal static string ItemCanon(Equipment.Item i) =>
        new StringBuilder(i.Id).Append('|').Append(i.Name)
            .Append('|').Append(i.Slot.ToString())
            .Append('|').Append(Num(i.Mods.MaxHp)).Append('|').Append(Num(i.Mods.Attack))
            .Append('|').Append(Num(i.Mods.Magic)).Append('|').Append(Num(i.Mods.Defense))
            .Append('|').Append(Num(i.Mods.Speed)).Append('|').Append(Num(i.Mods.CritPercent))
            .Append('|').Append(Num(i.PriceSats)).Append('|').Append(Num(i.MinLevel))
            .Append('|').Append(i.Counters?.ToString() ?? "-")
            .Append('|').Append(Num(i.VarianceBonus))
            .ToString();

    /// <summary>An integral value as invariant digits — no locale digit shapes, signs, or group separators.</summary>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A boolean as 1/0 — no culture, no casing question.</summary>
    private static string Flag(bool value) => value ? "1" : "0";

    /// <summary>
    /// A double as its EXACT IEEE-754 bits in explicit little-endian order — the same writer
    /// <see cref="GameConfigVersion"/> uses. Unused today because the v1 content schema admits no
    /// floating-point value; it is here so that the moment one is authored, the correct encoding is
    /// already the obvious one to reach for rather than <c>value.ToString()</c>.
    /// </summary>
    internal static string Bits(double value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, BitConverter.DoubleToUInt64Bits(value));
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
