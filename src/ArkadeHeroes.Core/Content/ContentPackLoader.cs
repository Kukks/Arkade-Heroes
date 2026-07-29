using System.Reflection;
using System.Text.Json;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;

namespace ArkadeHeroes.Core.Content;

/// <summary>
/// Parses authored JSON into a <see cref="ContentPack"/> and REFUSES to return one that is not
/// publishable.
///
/// Two deliberate choices:
///
/// * The parse is written against <see cref="JsonDocument"/> rather than
///   <c>JsonSerializer.Deserialize&lt;T&gt;</c>. Reflection-based binding needs the trimmer to be told to
///   keep the shapes it binds, and this assembly is published into a trimmed Blazor WebAssembly bundle
///   where a silently-trimmed constructor would surface as content that loads with zeroed stats. An
///   explicit reader has no such failure mode, and it also lets every field state its own rule.
///
/// * UNKNOWN FIELDS ARE AN ERROR. A misspelled "atack" would otherwise bind nothing and publish a weapon
///   with no attack at all — a silent balance change of exactly the kind this whole rung exists to make
///   impossible. Every field an author writes must be one the loader actually reads.
/// </summary>
public static class ContentPackLoader
{
    private const string ItemsResource = "ArkadeHeroes.Core.Content.items.json";
    private const string DungeonsResource = "ArkadeHeroes.Core.Content.dungeons.json";
    private const string SealsResource = "ArkadeHeroes.Core.Content.published-items.json";

    /// <summary>The pack compiled into this assembly. Embedded rather than read from disk so the server
    /// and the browser client load byte-identical content with no file I/O and nothing to divert.</summary>
    public static ContentPack LoadEmbedded()
    {
        var asm = typeof(ContentPackLoader).Assembly;
        return Parse(Read(asm, ItemsResource), Read(asm, DungeonsResource), Read(asm, SealsResource));
    }

    /// <summary>Parse and validate. Throws <see cref="ContentValidationException"/> on any malformed JSON,
    /// unknown field, or broken invariant — bad content must never load silently.</summary>
    public static ContentPack Parse(string itemsJson, string dungeonsJson, string? sealsJson = null)
    {
        var errors = new List<ContentError>();
        string packId = "core";
        var items = new List<Item>();
        var dungeons = new List<Dungeon>();

        try
        {
            using var doc = JsonDocument.Parse(itemsJson);
            var root = doc.RootElement;
            RejectUnknown(root, "items file", ["packId", "items"], errors);
            if (root.TryGetProperty("packId", out var pid) && pid.ValueKind == JsonValueKind.String)
                packId = pid.GetString()!;
            foreach (var el in Array(root, "items", errors))
                if (ReadItem(el, errors) is { } item) items.Add(item);
        }
        catch (JsonException ex)
        {
            errors.Add(new ContentError("json-malformed-items", ex.Message));
        }

        try
        {
            using var doc = JsonDocument.Parse(dungeonsJson);
            var root = doc.RootElement;
            RejectUnknown(root, "dungeons file", ["dungeons"], errors);
            foreach (var el in Array(root, "dungeons", errors))
                if (ReadDungeon(el, errors) is { } dungeon) dungeons.Add(dungeon);
        }
        catch (JsonException ex)
        {
            errors.Add(new ContentError("json-malformed-dungeons", ex.Message));
        }

        Dictionary<string, string>? seals = null;
        if (sealsJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(sealsJson);
                seals = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in doc.RootElement.EnumerateObject())
                    seals[p.Name] = p.Value.GetString() ?? "";
            }
            catch (JsonException ex)
            {
                errors.Add(new ContentError("json-malformed-seals", ex.Message));
            }
        }

        // A structural failure makes every later rule meaningless, so report it alone rather than burying
        // it under the cascade of "unknown drop item" it would cause.
        if (errors.Count > 0) throw new ContentValidationException(errors);

        var pack = new ContentPack(packId, items, dungeons);
        ContentValidation.ThrowIfInvalid(pack, GameConfig.Default, seals);
        return pack;
    }

    // ── readers ──────────────────────────────────────────────────────────────────────────────────

    private static Item? ReadItem(JsonElement el, List<ContentError> errors)
    {
        RejectUnknown(el, "item",
            ["id", "name", "slot", "mods", "priceSats", "minLevel", "counters", "varianceBonus"], errors);

        var id = Str(el, "id", errors, required: true) ?? "";
        var name = Str(el, "name", errors, required: true) ?? "";

        var slotText = Str(el, "slot", errors, required: true);
        if (!TryParseName<EquipmentSlot>(slotText, out var slot))
        {
            errors.Add(new ContentError("unknown-slot",
                $"item '{id}' has slot '{slotText}'; expected one of {string.Join(", ", Enum.GetNames<EquipmentSlot>())}"));
            return null;
        }

        var mods = new StatMods();
        if (el.TryGetProperty("mods", out var m))
        {
            RejectUnknown(m, $"item '{id}' mods",
                ["maxHp", "attack", "magic", "defense", "speed", "critPercent"], errors);
            mods = new StatMods(
                (int)Num(m, "maxHp", errors), (int)Num(m, "attack", errors), (int)Num(m, "magic", errors),
                (int)Num(m, "defense", errors), (int)Num(m, "speed", errors), (int)Num(m, "critPercent", errors));
        }

        CombatShape? counters = null;
        if (el.TryGetProperty("counters", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            if (!TryParseName<CombatShape>(c.GetString(), out var shape))
            {
                errors.Add(new ContentError("unknown-counter-shape",
                    $"item '{id}' counters '{c.GetString()}'; expected one of " +
                    string.Join(", ", Enum.GetNames<CombatShape>())));
                return null;
            }
            counters = shape;
        }

        return new Item(id, name, slot, mods,
            Num(el, "priceSats", errors, required: true),
            el.TryGetProperty("minLevel", out _) ? (int)Num(el, "minLevel", errors) : 1,
            counters,
            (int)Num(el, "varianceBonus", errors));
    }

    private static Dungeon? ReadDungeon(JsonElement el, List<ContentError> errors)
    {
        RejectUnknown(el, "dungeon",
            ["id", "name", "entryFeeBonusSats", "xpLevelCap", "dropRequiresFullClear", "dropRoll", "waves", "drops"],
            errors);

        var id = Str(el, "id", errors, required: true) ?? "";
        var name = Str(el, "name", errors, required: true) ?? "";

        // Every money-adjacent knob is REQUIRED. A dungeon that forgot its entry premium or its XP cap
        // would otherwise inherit a silent default and quietly become the cheapest farm in the game.
        var feeBonus = Num(el, "entryFeeBonusSats", errors, required: true);
        var xpCap = (int)Num(el, "xpLevelCap", errors, required: true);
        var fullClear = Bool(el, "dropRequiresFullClear", errors, required: true);

        var rollText = Str(el, "dropRoll", errors, required: true);
        if (!TryParseName<DropRoll>(rollText, out var roll))
        {
            errors.Add(new ContentError("unknown-drop-roll",
                $"dungeon '{id}' uses dropRoll '{rollText}'; expected one of {string.Join(", ", Enum.GetNames<DropRoll>())}"));
            return null;
        }

        var waves = new List<DungeonWave>();
        foreach (var w in Array(el, "waves", errors, required: true))
        {
            RejectUnknown(w, $"dungeon '{id}' wave", ["wave", "levelOffset", "xp", "ghostGear"], errors);
            var gear = new List<string>();
            foreach (var g in Array(w, "ghostGear", errors, required: true))
                gear.Add(g.GetString() ?? "");
            waves.Add(new DungeonWave(
                (int)Num(w, "wave", errors, required: true),
                (int)Num(w, "levelOffset", errors, required: true),
                Num(w, "xp", errors, required: true),
                gear));
        }

        var drops = new List<DungeonDrop>();
        foreach (var d in Array(el, "drops", errors, required: true))
        {
            RejectUnknown(d, $"dungeon '{id}' drop", ["itemId", "weight"], errors);
            drops.Add(new DungeonDrop(
                Str(d, "itemId", errors, required: true) ?? "",
                (int)Num(d, "weight", errors, required: true)));
        }

        return new Dungeon(id, name, feeBonus, xpCap, fullClear, roll, waves, drops);
    }

    // ── primitives ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds an enum by its exact member NAME and nothing else.
    ///
    /// <see cref="Enum.TryParse{T}(string, bool, out T)"/> deliberately also accepts a NUMERIC string, so
    /// <c>"slot": "0"</c> would bind to whichever member happens to sit at ordinal 0 today — and a later
    /// reorder of the enum would then silently re-point published content without a single authored file
    /// changing. The canonical writer hashes member names for exactly that reason, so the loader has to
    /// read them the same way. Case-sensitive too: <c>"weapon"</c> is a typo, not a synonym.
    /// </summary>
    private static bool TryParseName<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        return text is not null
               && Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)
               && Enum.TryParse(text, ignoreCase: false, out value);
    }

    /// <summary>Every property an author wrote must be one this loader reads. See the type doc: a
    /// misspelled field is a silent balance change, so it is refused rather than ignored.</summary>
    private static void RejectUnknown(JsonElement el, string what, string[] known, List<ContentError> errors)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        foreach (var p in el.EnumerateObject())
            if (!known.Contains(p.Name, StringComparer.Ordinal))
                errors.Add(new ContentError("unknown-field",
                    $"{what} has an unrecognised field '{p.Name}' — it would be silently ignored. " +
                    $"Known fields: {string.Join(", ", known)}"));
    }

    /// <summary>Materialises an array field. Returns a list rather than the struct enumerator so a missing
    /// field yields a plainly-empty result instead of a default enumerator whose behaviour would be a
    /// detail of the JSON reader.</summary>
    private static List<JsonElement> Array(
        JsonElement el, string name, List<ContentError> errors, bool required = false)
    {
        var result = new List<JsonElement>();
        JsonElement arr = default;
        var present = el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out arr);
        if (present && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray()) result.Add(e);
            return result;
        }
        if (required || present)
            errors.Add(new ContentError("missing-array", $"expected an array field '{name}'"));
        return result;
    }

    private static string? Str(JsonElement el, string name, List<ContentError> errors, bool required = false)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (required) errors.Add(new ContentError("missing-field", $"expected a string field '{name}'"));
        return null;
    }

    private static bool Bool(JsonElement el, string name, List<ContentError> errors, bool required = false)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        if (required) errors.Add(new ContentError("missing-field", $"expected a boolean field '{name}'"));
        return false;
    }

    /// <summary>An INTEGER field. Authored numbers are integers by design — a drop weight is a weight, not
    /// a percentage — so a fractional literal is refused rather than truncated into a different balance.</summary>
    private static long Num(JsonElement el, string name, List<ContentError> errors, bool required = false)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var n)) return n;
            errors.Add(new ContentError("non-integer-number",
                $"field '{name}' is {v.GetRawText()}; authored numbers must be integers"));
            return 0;
        }
        if (required) errors.Add(new ContentError("missing-field", $"expected an integer field '{name}'"));
        return 0;
    }

    private static string Read(Assembly asm, string resource)
    {
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new ContentValidationException(
                [new ContentError("missing-resource", $"embedded content resource '{resource}' is not in the build")]);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
