using System.Globalization;
using System.Reflection;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Core.Equipment;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The version identifier a content pack is stamped with. Gear stats feed combat resolution and combat is
/// client-verifiable, so this id answers the same question <see cref="GameConfigVersion"/> does — "which
/// CONTENT was this resolved under?" — and it has to be identical on every host and in every locale, or a
/// client would compute a different id for the same content and every replay it fetched would 404.
/// </summary>
public class ContentPackVersionTests
{
    private static ContentPack Pack => ContentPack.Default;

    /// <summary>
    /// THIS TYPE MUST HAVE NO STATIC CONSTRUCTOR, because <see cref="ContentPack"/>'s own static
    /// constructor calls into it and a cycle between the two is unresolvable.
    ///
    /// The cycle: <c>ContentPack..cctor</c> → <c>ContentPackLoader.LoadEmbedded</c> → <c>Parse</c> →
    /// <c>ContentValidation.Validate</c> → <c>ContentValidation.Seal</c> → <c>ItemCanon</c>, which is a
    /// static method ON THIS TYPE. If reaching it runs a static constructor here, that constructor reads
    /// <c>ContentPack.Default</c> — whose initializer is the one still running — gets NULL, and dies with a
    /// NullReferenceException wrapped in a TypeInitializationException that then poisons every type that
    /// touches the content pack, <c>Gauntlet</c> and <c>Trials</c> among them.
    ///
    /// It is asserted STRUCTURALLY, not by running the cycle, because whether the cycle actually fires is a
    /// property of the RUNTIME rather than of the code. A static field initializer with no explicit
    /// constructor compiles to a <c>beforefieldinit</c> type, and beforefieldinit only promises the
    /// initializer runs before the first static FIELD access — CoreCLR defers it past a static method call,
    /// Mono (which is what Blazor WebAssembly runs) does not. So on this test host the cycle is invisible
    /// and in the browser it is fatal, which is exactly how it shipped. No test running on CoreCLR can
    /// catch it by executing it; the shape is what has to be pinned.
    /// </summary>
    [Fact]
    public void TheVersionTypeHasNoStaticConstructorToCycleWithTheContentPack()
    {
        Assert.Null(typeof(ContentPackVersion).TypeInitializer);
    }

    [Fact]
    public void Compute_IsDeterministicAndPinnedToDefault()
    {
        Assert.Equal(ContentPackVersion.Compute(Pack), ContentPackVersion.Compute(Pack));
        Assert.Equal(ContentPackVersion.Default, ContentPackVersion.Compute(Pack));
        Assert.Equal(64, ContentPackVersion.Default.Length);
        Assert.Equal(ContentPackVersion.Default.ToLowerInvariant(), ContentPackVersion.Default);
        Assert.Matches("^[0-9a-f]{64}$", ContentPackVersion.Default);
    }

    /// <summary>
    /// The id is deliberately NOT pinned to a hard-coded hex literal. Authoring is add-only and the whole
    /// point of this rung is that publishing new content takes no code change — pinning the aggregate id
    /// would mean every new item forced a test edit. What must not move is what is already PUBLISHED, and
    /// that is enforced per item by the seal ledger and, for the pre-pack catalog, by
    /// <see cref="ContentPackGoldenVectorTests.TheShippedGearCatalogMatchesItsPreContentPackGoldenVector"/>.
    /// </summary>
    [Fact]
    public void Compute_IsStableAcrossRepeatedComputationAndFreshLoads()
    {
        var first = ContentPackVersion.Compute(Pack);
        for (var i = 0; i < 20; i++)
            Assert.Equal(first, ContentPackVersion.Compute(Pack));

        // A pack parsed again from the same bytes must land on the same id — the property a client relies
        // on when it re-derives the id of content it was served.
        Assert.Equal(first, ContentPackVersion.Compute(ContentPackLoader.LoadEmbedded()));
    }

    [Fact]
    public void Compute_IsCultureInvariant()
    {
        // The bug class this guards, and one this repo has actually been bitten by: a locale whose decimal
        // separator, digit shapes, minus sign or casing rules differ would otherwise give a client a
        // DIFFERENT id for the SAME content. tr-TR is in the list specifically for the dotted/dotless i —
        // enum member names and item ids are lowercased on the way to hex, and Turkish makes that a trap.
        var expected = ContentPackVersion.Compute(Pack);
        var negatives = ContentPackLoader.Parse(NegativeStatItems, MinimalDungeons);
        var expectedNegatives = ContentPackVersion.Compute(negatives);

        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "tr-TR", "fr-FR", "ar-SA", "th-TH" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(expected, ContentPackVersion.Compute(Pack));
                // Negative and large values are where a group separator or a locale minus sign would show up.
                Assert.Equal(expectedNegatives, ContentPackVersion.Compute(negatives));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>Items carrying negative and large stats — the shapes where a locale's minus sign or group
    /// separator would leak into a text encoding.</summary>
    private const string NegativeStatItems = """
        {
          "packId": "culture",
          "items": [
            { "id": "culture-a", "name": "Culture A", "slot": "Weapon", "priceSats": 1234567, "minLevel": 1,
              "mods": { "maxHp": -1500, "attack": 1000000, "speed": -3 } }
          ]
        }
        """;

    private const string MinimalDungeons = """{ "dungeons": [] }""";

    /// <summary>
    /// Every authored value must move the id. If one did not, two packs that RESOLVE differently would
    /// share a stamp and the endpoint would hand a verifier the wrong content with full confidence — the
    /// severe failure, because the verifier would then render "SERVER CHEATED" over an honest result.
    /// </summary>
    [Theory]
    [InlineData("id", "\"id\": \"culture-a\"", "\"id\": \"culture-b\"")]
    [InlineData("name", "\"name\": \"Culture A\"", "\"name\": \"Culture Z\"")]
    [InlineData("slot", "\"slot\": \"Weapon\"", "\"slot\": \"Armor\"")]
    [InlineData("priceSats", "\"priceSats\": 1234567", "\"priceSats\": 1234568")]
    [InlineData("minLevel", "\"minLevel\": 1", "\"minLevel\": 2")]
    [InlineData("maxHp", "\"maxHp\": -1500", "\"maxHp\": -1501")]
    [InlineData("attack", "\"attack\": 1000000", "\"attack\": 1000001")]
    [InlineData("speed", "\"speed\": -3", "\"speed\": -4")]
    public void Compute_ChangesForEveryAuthoredItemField(string field, string from, string to)
    {
        var baseline = ContentPackVersion.Compute(ContentPackLoader.Parse(NegativeStatItems, MinimalDungeons));
        var perturbed = ContentPackVersion.Compute(
            ContentPackLoader.Parse(NegativeStatItems.Replace(from, to), MinimalDungeons));
        Assert.True(baseline != perturbed, $"changing an item's {field} did not change the content version");
    }

    [Fact]
    public void Compute_ChangesForTheCounterShapeAndTheVarianceBonus()
    {
        // The two fields that are read ONLY when gear counters are on. They still have to be in the id:
        // a pack that differs only in a counter shape resolves different fights the moment the flag flips,
        // and a stamp that could not tell those packs apart would strand every replay taken across it.
        var plain = ContentPackVersion.Compute(ContentPackLoader.Parse(NegativeStatItems, MinimalDungeons));

        var countered = NegativeStatItems.Replace("\"minLevel\": 1,", "\"minLevel\": 1, \"counters\": \"Bulk\",");
        Assert.NotEqual(plain, ContentPackVersion.Compute(ContentPackLoader.Parse(countered, MinimalDungeons)));

        var wildcard = NegativeStatItems.Replace("\"minLevel\": 1,", "\"minLevel\": 1, \"varianceBonus\": 25,");
        Assert.NotEqual(plain, ContentPackVersion.Compute(ContentPackLoader.Parse(wildcard, MinimalDungeons)));
    }

    [Theory]
    [InlineData("entryFeeBonusSats", "\"entryFeeBonusSats\": 250", "\"entryFeeBonusSats\": 251")]
    [InlineData("xpLevelCap", "\"xpLevelCap\": 10", "\"xpLevelCap\": 11")]
    [InlineData("dropRequiresFullClear", "\"dropRequiresFullClear\": true", "\"dropRequiresFullClear\": false")]
    [InlineData("dropRoll", "\"dropRoll\": \"DeterministicRng\"", "\"dropRoll\": \"EntropyByte\"")]
    [InlineData("levelOffset", "\"levelOffset\": -1", "\"levelOffset\": -2")]
    [InlineData("wave xp", "\"xp\": 15", "\"xp\": 16")]
    [InlineData("ghostGear", "\"ghostGear\": []", "\"ghostGear\": [\"dungeon-item\"]")]
    [InlineData("drop weight", "\"weight\": 1", "\"weight\": 2")]
    [InlineData("dungeon id", "\"id\": \"pit\"", "\"id\": \"pit2\"")]
    [InlineData("dungeon name", "\"name\": \"The Pit\"", "\"name\": \"The Hole\"")]
    public void Compute_ChangesForEveryAuthoredDungeonField(string field, string from, string to)
    {
        var baseline = ContentPackVersion.Compute(ContentPackLoader.Parse(DungeonItems, OneDungeon));
        var perturbed = ContentPackVersion.Compute(
            ContentPackLoader.Parse(DungeonItems, OneDungeon.Replace(from, to)));
        Assert.True(baseline != perturbed, $"changing a dungeon's {field} did not change the content version");
    }

    private const string DungeonItems = """
        {
          "packId": "dungeon",
          "items": [
            { "id": "dungeon-item", "name": "Dungeon Item", "slot": "Trinket", "priceSats": 100, "minLevel": 1,
              "mods": { "critPercent": 1 } }
          ]
        }
        """;

    private const string OneDungeon = """
        {
          "dungeons": [
            {
              "id": "pit", "name": "The Pit",
              "entryFeeBonusSats": 250, "xpLevelCap": 10,
              "dropRequiresFullClear": true, "dropRoll": "DeterministicRng",
              "waves": [ { "wave": 1, "levelOffset": -1, "xp": 15, "ghostGear": [] } ],
              "drops": [ { "itemId": "dungeon-item", "weight": 1 } ]
            }
          ]
        }
        """;

    [Fact]
    public void Compute_ChangesWhenTheAuthoredORDERChanges()
    {
        // Order is not cosmetic. A drop table's weights are walked in order, so reordering the lines can
        // change which item a given entropy value selects — that has to be a NEW version rather than a
        // silent re-point of an existing one.
        var twoDrops = OneDungeon.Replace(
            """[ { "itemId": "dungeon-item", "weight": 1 } ]""",
            """[ { "itemId": "dungeon-item", "weight": 1 }, { "itemId": "dungeon-other", "weight": 1 } ]""");
        var swapped = OneDungeon.Replace(
            """[ { "itemId": "dungeon-item", "weight": 1 } ]""",
            """[ { "itemId": "dungeon-other", "weight": 1 }, { "itemId": "dungeon-item", "weight": 1 } ]""");
        var items = DungeonItems.Replace("\"items\": [",
            "\"items\": [ { \"id\": \"dungeon-other\", \"name\": \"Other\", \"slot\": \"Trinket\", " +
            "\"priceSats\": 100, \"mods\": { \"speed\": 1 } },");

        Assert.NotEqual(
            ContentPackVersion.Compute(ContentPackLoader.Parse(items, twoDrops)),
            ContentPackVersion.Compute(ContentPackLoader.Parse(items, swapped)));
    }

    [Fact]
    public void TheContentVersionIsNotTheConfigVersion()
    {
        // Two separate id SPACES, domain-tagged apart. Folding content into the config id would have
        // changed GameConfigVersion.Default and stranded every replay already stamped with it.
        Assert.NotEqual(GameConfigVersion.Default, ContentPackVersion.Default);
    }

    /// <summary>
    /// The endianness guard, enforced structurally rather than by hoping.
    ///
    /// The v1 content schema admits no floating-point value — a drop chance is an integer WEIGHT — which is
    /// why the canonical writer needs no bit-level encoding on the paying path. This test fails the moment
    /// a double or float is added anywhere in the pack's shape, pointing whoever added it at
    /// <c>ContentPackVersion.Bits</c>, the little-endian IEEE-754 writer <see cref="GameConfigVersion"/>
    /// uses. Without it, someone would eventually author a double, format it as text, and reintroduce
    /// exactly the culture bug the rest of this file exists to prevent.
    /// </summary>
    [Fact]
    public void TheContentSchemaAdmitsNoFloatingPointValue()
    {
        var seen = new HashSet<Type>();
        var offenders = new List<string>();
        Inspect(typeof(ContentPack), seen, offenders);
        Inspect(typeof(Item), seen, offenders);

        Assert.True(offenders.Count == 0,
            "the content schema now contains floating-point values:\n  " + string.Join("\n  ", offenders) +
            "\nWrite them through ContentPackVersion.Bits (little-endian IEEE-754), never as text, and " +
            "then update this test.");
    }

    private static void Inspect(Type type, HashSet<Type> seen, List<string> offenders)
    {
        if (!seen.Add(type)) return;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = Unwrap(p.PropertyType);
            if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
                offenders.Add($"{type.Name}.{p.Name} is {t.Name}");
            else if (t.Namespace?.StartsWith("ArkadeHeroes", StringComparison.Ordinal) == true)
                Inspect(t, seen, offenders);
        }
    }

    /// <summary>Unwraps Nullable&lt;T&gt; and the element type of a collection, so a
    /// <c>IReadOnlyList&lt;SomethingWithADouble&gt;</c> is inspected rather than skipped.</summary>
    private static Type Unwrap(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null) return underlying;
        if (t.IsGenericType && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0];
        return t;
    }

    [Fact]
    public void EveryEnumInTheSchemaHashesByNameSoAReorderCannotSilentlyRepointContent()
    {
        // Ordinals are the trap: inserting a member reuses a number and would quietly turn every
        // "Trinket" into an "Armor" without changing a single authored file. The writer uses ToString(),
        // and this pins that the NAMES are what the id is sensitive to.
        var weapon = ContentPackLoader.Parse(NegativeStatItems, MinimalDungeons);
        var armor = ContentPackLoader.Parse(
            NegativeStatItems.Replace("\"slot\": \"Weapon\"", "\"slot\": \"Armor\""), MinimalDungeons);

        Assert.Equal(EquipmentSlot.Weapon, weapon.Items[0].Slot);
        Assert.Equal(EquipmentSlot.Armor, armor.Items[0].Slot);
        Assert.NotEqual(ContentPackVersion.Compute(weapon), ContentPackVersion.Compute(armor));

        // And the loader binds by NAME too — an ordinal in the JSON is refused, not silently accepted.
        Assert.ThrowsAny<ContentValidationException>(() =>
            ContentPackLoader.Parse(NegativeStatItems.Replace("\"slot\": \"Weapon\"", "\"slot\": \"0\""),
                MinimalDungeons));
    }

    [Fact]
    public void TheSealIsSensitiveToEveryFieldTheVersionIdIs()
    {
        // The seal and the version id must cover the same bytes — they share ContentPackVersion.ItemCanon
        // for exactly that reason, and this is the check that they have not drifted apart.
        var item = ItemCatalog.Find("arkforged-edge")!;
        var reseal = ContentValidation.Seal(item);
        Assert.Equal(reseal, ContentValidation.Seal(item));
        Assert.Matches("^[0-9a-f]{64}$", reseal);

        foreach (var variant in new[]
                 {
                     item with { Name = "Arkforged Blade" },
                     item with { PriceSats = item.PriceSats + 1 },
                     item with { MinLevel = item.MinLevel + 1 },
                     item with { Mods = item.Mods with { Attack = item.Mods.Attack + 1 } },
                     item with { Slot = EquipmentSlot.Armor },
                     item with { Counters = CombatShape.Bulk },
                     item with { VarianceBonus = 5 },
                 })
            Assert.NotEqual(reseal, ContentValidation.Seal(variant));
    }
}
