using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Core.Content;

/// <summary>One reason a pack is not publishable. <see cref="Code"/> is a stable machine-readable tag so
/// tests (and, later, an authoring UI) can assert on the RULE that fired rather than on its prose.</summary>
public sealed record ContentError(string Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

/// <summary>Thrown when a pack fails validation. Loading bad content is a hard failure, never a warning:
/// a dungeon whose drop is worth more than its entry fee drains real bitcoin on every clear.</summary>
public sealed class ContentValidationException(IReadOnlyList<ContentError> errors)
    : Exception("Content pack is not publishable:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)))
{
    public IReadOnlyList<ContentError> Errors { get; } = errors;
}

/// <summary>
/// The PUBLISH-TIME GUARD over all authored content.
///
/// Its reason for existing is the treasury. Sats are real bitcoin, the treasury cannot print them, and
/// <c>Gauntlet</c> states the invariant that keeps PvE from becoming a faucet: entry costs MORE than the
/// best item the run can drop, so a full-clear farm is EV-positive for the treasury at ANY clear rate.
/// While there was one hand-written pool, one test over that pool was enough. Once dungeons are authored
/// data, a typo in a drop table is a money leak, so the guarantee has to be a check over ALL content that
/// fails LOUDLY — and it runs in the LOADER, not only in the test suite, so bad content cannot reach a
/// player even if it somehow reached a deployment.
///
/// The treasury rule is kept in its ABSOLUTE form — best POSSIBLE drop, not expected value. Weakening it
/// to an EV comparison would let a dungeon dangle a jackpot far above its entry fee as long as the odds
/// were long enough, which is a different risk posture and one the owner has not chosen.
/// </summary>
public static class ContentValidation
{
    /// <summary>Ceiling on a single drop line's weight. Not a balance opinion — a bound that keeps the
    /// weight arithmetic far from overflow and turns a fat-fingered extra digit into a loud failure.</summary>
    public const int MaxDropWeight = 1_000_000;

    /// <summary>Ceiling on a drop table's total weight, same reasoning.</summary>
    public const int MaxTotalDropWeight = 10_000_000;

    /// <summary>The domain tag for an item's add-only SEAL. Distinct from the pack tag so a seal can never
    /// be confused with a version id.</summary>
    private const string SealTag = "arkade-item-seal-v1";

    /// <summary>
    /// The add-only SEAL of one item: a domain-tagged SHA-256 over exactly the bytes the version id covers
    /// for that item. Two packs that agree on an item's seal agree on every stat a player holds.
    /// </summary>
    public static string Seal(Equipment.Item item) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            SealTag + "\n" + ContentPackVersion.ItemCanon(item)))).ToLowerInvariant();

    /// <summary>
    /// Every reason <paramref name="pack"/> is not publishable, or an empty list.
    ///
    /// <paramref name="config"/> is the economy the pack will actually be resolved under — it sets the
    /// match fee the treasury rule prices entry against, so a server that retunes its fees downward must
    /// re-check its content against ITS OWN config rather than against the compiled-in default.
    ///
    /// <paramref name="publishedSeals"/> is the add-only ledger: item id to <see cref="Seal"/>. Any item
    /// whose id is already in it MUST still hash to the recorded seal — that is the enforcement of "a
    /// published item is immutable; a change means a new id". Ids absent from the ledger are new
    /// publications, which is the one edit add-only permits.
    /// </summary>
    public static IReadOnlyList<ContentError> Validate(
        ContentPack pack,
        GameConfig? config = null,
        IReadOnlyDictionary<string, string>? publishedSeals = null)
    {
        var cfg = config ?? GameConfig.Default;
        var errors = new List<ContentError>();

        // ── Items ────────────────────────────────────────────────────────────────────────────────
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pack.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                errors.Add(new ContentError("empty-item-id", "an item has a blank id"));
            else if (!seenItems.Add(item.Id))
                errors.Add(new ContentError("duplicate-item-id",
                    $"item '{item.Id}' is defined more than once — two players holding it would not agree on its stats"));

            if (string.IsNullOrWhiteSpace(item.Name))
                errors.Add(new ContentError("empty-item-name", $"item '{item.Id}' has a blank name"));
            if (item.PriceSats < 0)
                errors.Add(new ContentError("negative-item-price", $"item '{item.Id}' is priced {item.PriceSats} sat"));
            if (item.MinLevel < 1)
                errors.Add(new ContentError("bad-item-min-level",
                    $"item '{item.Id}' has minLevel {item.MinLevel}; the floor is 1"));
            if (item.VarianceBonus < 0)
                errors.Add(new ContentError("negative-variance-bonus",
                    $"item '{item.Id}' has varianceBonus {item.VarianceBonus}"));

            // Add-only: a published id may never change what it means.
            if (publishedSeals is not null && publishedSeals.TryGetValue(item.Id, out var published))
            {
                var seal = Seal(item);
                if (!seal.Equals(published, StringComparison.OrdinalIgnoreCase))
                    errors.Add(new ContentError("redefined-item",
                        $"item '{item.Id}' was already published as {published[..12]} but now seals to " +
                        $"{seal[..12]} — a published item is immutable; publish a NEW id instead"));
            }
        }

        // ── Dungeons ─────────────────────────────────────────────────────────────────────────────
        var seenDungeons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dungeon in pack.Dungeons)
        {
            if (string.IsNullOrWhiteSpace(dungeon.Id))
                errors.Add(new ContentError("empty-dungeon-id", "a dungeon has a blank id"));
            else if (!seenDungeons.Add(dungeon.Id))
                errors.Add(new ContentError("duplicate-dungeon-id", $"dungeon '{dungeon.Id}' is defined more than once"));

            if (dungeon.EntryFeeBonusSats < 0)
                errors.Add(new ContentError("negative-entry-fee-bonus",
                    $"dungeon '{dungeon.Id}' has entryFeeBonusSats {dungeon.EntryFeeBonusSats}"));

            ValidateWaves(dungeon, pack, errors);
            ValidateDropTable(dungeon, pack, errors);
            ValidateTreasuryPositive(dungeon, pack, cfg, errors);
        }

        return errors;
    }

    private static void ValidateWaves(Dungeon dungeon, ContentPack pack, List<ContentError> errors)
    {
        if (dungeon.Waves.Count == 0)
        {
            errors.Add(new ContentError("no-waves", $"dungeon '{dungeon.Id}' has no waves"));
            return;
        }

        // The resolver walks waves 1..WaveCount, so the authored numbers must be exactly that run with no
        // gap and no repeat — otherwise a wave would silently resolve with a zero offset and no gear.
        for (var i = 0; i < dungeon.Waves.Count; i++)
            if (dungeon.Waves[i].Wave != i + 1)
                errors.Add(new ContentError("non-contiguous-waves",
                    $"dungeon '{dungeon.Id}' wave #{i + 1} is numbered {dungeon.Waves[i].Wave} — waves must " +
                    "be 1..N in order, because that is the order the ladder is resolved in"));

        foreach (var wave in dungeon.Waves)
        {
            if (wave.Xp < 0)
                errors.Add(new ContentError("negative-wave-xp",
                    $"dungeon '{dungeon.Id}' wave {wave.Wave} pays {wave.Xp} xp"));

            foreach (var gearId in wave.GhostGear)
                if (pack.FindItem(gearId) is null)
                    errors.Add(new ContentError("unknown-ghost-gear-item",
                        $"dungeon '{dungeon.Id}' wave {wave.Wave} equips its ghost with '{gearId}', which no " +
                        "item defines"));
        }
    }

    private static void ValidateDropTable(Dungeon dungeon, ContentPack pack, List<ContentError> errors)
    {
        if (dungeon.Drops.Count == 0) return;   // a dungeon that pays no item is legitimate (see Trials)

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var drop in dungeon.Drops)
        {
            if (pack.FindItem(drop.ItemId) is null)
                errors.Add(new ContentError("unknown-drop-item",
                    $"dungeon '{dungeon.Id}' can drop '{drop.ItemId}', which no item defines"));
            if (!seen.Add(drop.ItemId))
                errors.Add(new ContentError("duplicate-drop-line",
                    $"dungeon '{dungeon.Id}' lists '{drop.ItemId}' in its drop table twice — merge the weights " +
                    "so the authored chance reads as the real one"));

            if (drop.Weight < 0)
                errors.Add(new ContentError("negative-drop-weight",
                    $"dungeon '{dungeon.Id}' gives '{drop.ItemId}' weight {drop.Weight}"));
            else if (drop.Weight > MaxDropWeight)
                errors.Add(new ContentError("drop-weight-too-large",
                    $"dungeon '{dungeon.Id}' gives '{drop.ItemId}' weight {drop.Weight}, over the {MaxDropWeight} " +
                    "ceiling — check for an extra digit"));

            total += Math.Max(0, drop.Weight);
        }

        if (total <= 0)
            errors.Add(new ContentError("zero-total-drop-weight",
                $"dungeon '{dungeon.Id}' has a drop table whose weights sum to 0 — no line can ever be picked, " +
                "so the table is either dead or a typo"));
        else if (total > MaxTotalDropWeight)
            errors.Add(new ContentError("total-drop-weight-too-large",
                $"dungeon '{dungeon.Id}' drop weights sum to {total}, over the {MaxTotalDropWeight} ceiling"));
    }

    /// <summary>
    /// THE money guard. Entry must exceed the value of the best item the dungeon can possibly drop, at
    /// EVERY level a hero can enter at — so the run is EV-positive for the treasury whatever the clear
    /// rate, and PvE stays a sats sink rather than a faucet.
    ///
    /// Checked across the whole level range rather than only at level 1: the fee curve is
    /// <c>base + perLevel * level</c> today and so is cheapest at the bottom, but a retune to a negative
    /// or zero per-level term would move where the minimum sits, and the guard must not quietly depend on
    /// the shape of a config value it does not own.
    ///
    /// Only lines that can ACTUALLY drop count — a weight-0 line is unreachable, so it does not set the
    /// bar.
    /// </summary>
    private static void ValidateTreasuryPositive(
        Dungeon dungeon, ContentPack pack, GameConfig cfg, List<ContentError> errors)
    {
        long bestDrop = 0;
        var bestId = "";
        foreach (var drop in dungeon.Drops)
        {
            if (drop.Weight <= 0) continue;
            if (pack.FindItem(drop.ItemId) is not { } item) continue;   // already reported as unknown-drop-item
            if (item.PriceSats > bestDrop) { bestDrop = item.PriceSats; bestId = item.Id; }
        }
        if (bestDrop <= 0) return;   // drops nothing of value: nothing to leak

        for (var level = 1; level <= Math.Max(1, cfg.Curve.MaxLevel); level++)
        {
            var fee = Leveling.MatchFee(level, cfg) + dungeon.EntryFeeBonusSats;
            if (fee <= bestDrop)
            {
                errors.Add(new ContentError("treasury-negative-dungeon",
                    $"dungeon '{dungeon.Id}' costs {fee} sat to enter at level {level} but can drop " +
                    $"'{bestId}', worth {bestDrop} sat — farming it would drain the treasury. Entry must " +
                    "exceed the best POSSIBLE drop at every level."));
                return;   // one report per dungeon; the whole level range fails for the same reason
            }
        }
    }

    /// <summary>Validate or throw. This is what the loader calls, so unpublishable content cannot resolve
    /// a single match.</summary>
    public static void ThrowIfInvalid(
        ContentPack pack,
        GameConfig? config = null,
        IReadOnlyDictionary<string, string>? publishedSeals = null)
    {
        var errors = Validate(pack, config, publishedSeals);
        if (errors.Count > 0) throw new ContentValidationException(errors);
    }
}
