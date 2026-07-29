using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The publish-time guard over authored content, proven by REJECTION.
///
/// Every rule here is shown failing on content deliberately broken to trip exactly that rule. A validator
/// nobody has watched refuse anything is not a guard, it is a comment — and the rule these tests exist for
/// is the one that spends real money: a dungeon whose entry fee does not exceed its best possible drop is
/// a bitcoin faucet pointed at the treasury.
///
/// Rejection is asserted on the stable <see cref="ContentError.Code"/> rather than on the prose, so the
/// message can be improved without the guard silently ceasing to test anything.
/// </summary>
public class ContentValidationTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────
    //
    // A minimal PUBLISHABLE pack, and then one deliberate break per rule. Ids are deliberately unlike the
    // shipped ones so the add-only seal ledger has nothing to say about them.

    private const string GoodItems = """
        {
          "packId": "test",
          "items": [
            { "id": "test-trinket", "name": "Test Trinket", "slot": "Trinket", "priceSats": 100, "minLevel": 1,
              "mods": { "critPercent": 1 } },
            { "id": "test-crown", "name": "Test Crown", "slot": "Trinket", "priceSats": 10000, "minLevel": 10,
              "mods": { "maxHp": 50 } }
          ]
        }
        """;

    private const string GoodDungeons = """
        {
          "dungeons": [
            {
              "id": "test-pit", "name": "The Test Pit",
              "entryFeeBonusSats": 250, "xpLevelCap": 10,
              "dropRequiresFullClear": true, "dropRoll": "DeterministicRng",
              "waves": [
                { "wave": 1, "levelOffset": -1, "xp": 15, "ghostGear": [] },
                { "wave": 2, "levelOffset": 0, "xp": 20, "ghostGear": ["test-crown"] }
              ],
              "drops": [ { "itemId": "test-trinket", "weight": 1 } ]
            }
          ]
        }
        """;

    /// <summary>The drop table of the baseline fixture, as it appears verbatim in <see cref="GoodDungeons"/>
    /// — the anchor the drop-table breaks below swap out.</summary>
    private const string OneCheapDrop = """[ { "itemId": "test-trinket", "weight": 1 } ]""";

    /// <summary>Loads a pack, returning the error codes it was refused with (empty = it loaded).</summary>
    private static IReadOnlyList<string> Refusal(string items, string dungeons, string? seals = null)
    {
        try
        {
            ContentPackLoader.Parse(items, dungeons, seals);
            return [];
        }
        catch (ContentValidationException ex)
        {
            return ex.Errors.Select(e => e.Code).ToList();
        }
    }

    private static void Rejects(string expectedCode, string items, string dungeons, string? seals = null)
    {
        var codes = Refusal(items, dungeons, seals);
        Assert.True(codes.Contains(expectedCode),
            $"expected the loader to refuse this content with '{expectedCode}', but it answered: " +
            (codes.Count == 0 ? "IT LOADED CLEANLY" : string.Join(", ", codes)));
    }

    [Fact]
    public void TheBaselineFixtureIsPublishable_SoEveryRejectionBelowIsAboutItsOwnBreak()
    {
        // Without this, a fixture that was broken in some OTHER way would make every test below pass for
        // the wrong reason.
        Assert.Empty(Refusal(GoodItems, GoodDungeons));
    }

    // ── THE MONEY RULE ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADungeonWhoseBestDropOutvaluesItsEntryFeeIsRefused()
    {
        // The existential one. Entry at level 1 is MatchFee(1) + 250 = 770 sat; this pit can drop a
        // 10 000-sat crown. Every clear would be a net transfer OUT of a treasury holding real bitcoin.
        Rejects("treasury-negative-dungeon", GoodItems,
            GoodDungeons.Replace("""{ "itemId": "test-trinket", "weight": 1 }""",
                                 """{ "itemId": "test-crown", "weight": 1 }"""));
    }

    [Fact]
    public void ADungeonIsRefusedEvenWhenTheOverpricedDropIsAstronomicallyUnlikely()
    {
        // The rule is kept in its ABSOLUTE form on purpose: "best POSSIBLE drop", not expected value. A
        // one-in-a-million jackpot still has to be affordable, because EV-positive-on-average is a
        // different risk posture and not one the owner has chosen. This is the test that would start
        // failing the day someone quietly weakens the rule to an EV comparison.
        Rejects("treasury-negative-dungeon", GoodItems,
            GoodDungeons.Replace(OneCheapDrop,
                """[ { "itemId": "test-trinket", "weight": 999999 }, { "itemId": "test-crown", "weight": 1 } ]"""));
    }

    [Fact]
    public void AWeightZeroJackpotDoesNotTripTheTreasuryRule_BecauseItCanNeverBePicked()
    {
        // The other side of that coin: the bar is set by what can ACTUALLY drop. A weight-0 line is
        // unreachable, so it must not block publication — otherwise authors would learn to work around
        // the guard, which is worse than the guard being slightly loose here.
        Assert.Empty(Refusal(GoodItems,
            GoodDungeons.Replace(OneCheapDrop,
                """[ { "itemId": "test-trinket", "weight": 1 }, { "itemId": "test-crown", "weight": 0 } ]""")));
    }

    [Fact]
    public void TheShippedContentIsTreasuryPositiveForEveryDungeonAtEveryLevel()
    {
        // The shipped pack, under the compiled-in economy: entry must exceed the best possible drop at
        // every level a hero can enter at. This is PveGauntlet_CostsMoreThanItsBestDrop generalised from
        // one hand-written pool to all authored content.
        var cfg = GameConfig.Default;
        Assert.Empty(ContentValidation.Validate(ContentPack.Default, cfg));

        foreach (var dungeon in ContentPack.Default.Dungeons)
        {
            var best = dungeon.Drops.Where(d => d.Weight > 0)
                .Select(d => ContentPack.Default.FindItem(d.ItemId)!.PriceSats)
                .DefaultIfEmpty(0).Max();
            for (var level = 1; level <= cfg.Curve.MaxLevel; level++)
                Assert.True(Leveling.MatchFee(level, cfg) + dungeon.EntryFeeBonusSats > best,
                    $"dungeon '{dungeon.Id}' at level {level} does not out-price its {best}-sat best drop");
        }
    }

    [Fact]
    public void TheTreasuryRuleIsRecheckedAgainstARETUNEDEconomy_NotOnlyTheCompiledInOne()
    {
        // The hole a load-time-only check would leave: the loader validates against GameConfig.Default,
        // but an operator can retune match fees downward at runtime. Content that was safe under the
        // default economy can be a faucet under theirs, so the same rule has to be answerable for any
        // config — which is why Validate takes one.
        var starved = GameConfig.Default with { MatchFeeBaseSats = 1, MatchFeePerLevel = 0 };
        var codes = ContentValidation.Validate(ContentPack.Default, starved).Select(e => e.Code).ToList();
        Assert.Contains("treasury-negative-dungeon", codes);

        // …and the shipped economy is comfortably clear of that edge.
        Assert.Empty(ContentValidation.Validate(ContentPack.Default, GameConfig.Default));
    }

    // ── referential integrity ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADropPointingAtAnItemThatDoesNotExistIsRefused()
        => Rejects("unknown-drop-item", GoodItems, GoodDungeons.Replace("test-trinket", "no-such-item"));

    [Fact]
    public void GhostGearPointingAtAnItemThatDoesNotExistIsRefused()
        => Rejects("unknown-ghost-gear-item", GoodItems,
            GoodDungeons.Replace("""["test-crown"]""", """["no-such-item"]"""));

    [Fact]
    public void TwoItemsSharingAnIdAreRefused()
    {
        // "Two players holding the same item id must never have different stats" — within one pack, a
        // duplicated id is that failure in its purest form.
        Rejects("duplicate-item-id", GoodItems.Replace(
            """{ "id": "test-crown", "name": "Test Crown", "slot": "Trinket", "priceSats": 10000, "minLevel": 10,""",
            """{ "id": "test-trinket", "name": "Test Crown", "slot": "Trinket", "priceSats": 10000, "minLevel": 10,"""),
            GoodDungeons.Replace("""["test-crown"]""", """[]"""));
    }

    [Fact]
    public void TwoDungeonsSharingAnIdAreRefused()
    {
        var doubled = GoodDungeons.Replace("\"dungeons\": [", "\"dungeons\": [" + OneDungeon + ",");
        Rejects("duplicate-dungeon-id", GoodItems, doubled);
    }

    private const string OneDungeon = """
        {
          "id": "test-pit", "name": "The Test Pit",
          "entryFeeBonusSats": 250, "xpLevelCap": 10,
          "dropRequiresFullClear": true, "dropRoll": "DeterministicRng",
          "waves": [ { "wave": 1, "levelOffset": -1, "xp": 15, "ghostGear": [] } ],
          "drops": [ { "itemId": "test-trinket", "weight": 1 } ]
        }
        """;

    // ── add-only ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RepricingAnAlreadyPublishedItemIsRefused()
    {
        // The owner's rule: a published item is immutable and a "change" means a NEW id. The seal ledger
        // is what makes that enforceable — an id it already knows must still hash to the same stats.
        var published = ContentPackLoader.Parse(GoodItems, GoodDungeons);
        var seals = string.Join(",", published.Items.Select(
            i => $"\"{i.Id}\": \"{ContentValidation.Seal(i)}\""));
        var ledger = "{" + seals + "}";

        // Unchanged content still loads against its own ledger…
        Assert.Empty(Refusal(GoodItems, GoodDungeons, ledger));

        // …but moving a published item's price is refused, even though the pack is otherwise valid.
        Rejects("redefined-item", GoodItems.Replace("\"priceSats\": 100,", "\"priceSats\": 400,"),
            GoodDungeons, ledger);
    }

    [Fact]
    public void RestattingAnAlreadyPublishedItemIsRefused()
    {
        var published = ContentPackLoader.Parse(GoodItems, GoodDungeons);
        var ledger = "{" + string.Join(",", published.Items.Select(
            i => $"\"{i.Id}\": \"{ContentValidation.Seal(i)}\"")) + "}";

        // A single point of crit — the smallest possible edit to a shipped combat input.
        Rejects("redefined-item", GoodItems.Replace("\"critPercent\": 1", "\"critPercent\": 2"),
            GoodDungeons, ledger);
    }

    [Fact]
    public void PublishingAnEntirelyNewItemIsAllowed_BecauseTheRuleIsAddOnlyNotFrozen()
    {
        var published = ContentPackLoader.Parse(GoodItems, GoodDungeons);
        var ledger = "{" + string.Join(",", published.Items.Select(
            i => $"\"{i.Id}\": \"{ContentValidation.Seal(i)}\"")) + "}";

        var withNewItem = GoodItems.Replace("\"items\": [",
            "\"items\": [ { \"id\": \"test-newcomer\", \"name\": \"Newcomer\", \"slot\": \"Weapon\", " +
            "\"priceSats\": 700, \"mods\": { \"attack\": 3 } },");
        Assert.Empty(Refusal(withNewItem, GoodDungeons, ledger));
    }

    [Fact]
    public void EveryShippedItemIsRecordedInTheSealLedger()
    {
        // Completeness. The ledger only protects ids it knows, so an item that shipped without a seal is
        // an item anyone could silently restat. The loader cannot demand this (a hand-built pack has no
        // ledger), so the shipped pack is held to it here.
        var ledger = ShippedSeals();
        var missing = ContentPack.Default.Items.Where(i => !ledger.ContainsKey(i.Id)).ToList();

        // Report the WHOLE gap at once, as a paste-ready block: an author who added five items should not
        // have to run the suite five times to learn all five seals.
        Assert.True(missing.Count == 0,
            "these items ship without a seal — add them to Content/published-items.json:\n" +
            string.Join(",\n", missing.Select(i => $"  \"{i.Id}\": \"{ContentValidation.Seal(i)}\"")));

        foreach (var item in ContentPack.Default.Items)
            Assert.Equal(ContentValidation.Seal(item), ledger[item.Id]);
    }

    private static Dictionary<string, string> ShippedSeals()
    {
        using var stream = typeof(ContentPack).Assembly
            .GetManifestResourceStream("ArkadeHeroes.Core.Content.published-items.json")!;
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    // ── drop chances are well-formed ─────────────────────────────────────────────────────────────

    [Fact]
    public void ANegativeDropWeightIsRefused()
        => Rejects("negative-drop-weight", GoodItems, GoodDungeons.Replace("\"weight\": 1", "\"weight\": -1"));

    [Fact]
    public void ADropTableThatSumsToZeroIsRefused()
        => Rejects("zero-total-drop-weight", GoodItems, GoodDungeons.Replace("\"weight\": 1", "\"weight\": 0"));

    [Fact]
    public void AnAbsurdlyLargeDropWeightIsRefused()
        => Rejects("drop-weight-too-large", GoodItems, GoodDungeons.Replace("\"weight\": 1", "\"weight\": 99999999"));

    [Fact]
    public void ListingTheSameItemTwiceInOneDropTableIsRefused()
    {
        // Two lines for one item make the authored chance unreadable — the number an author sees is not
        // the number the pick uses.
        Rejects("duplicate-drop-line", GoodItems,
            GoodDungeons.Replace(OneCheapDrop,
                """[ { "itemId": "test-trinket", "weight": 1 }, { "itemId": "test-trinket", "weight": 3 } ]"""));
    }

    // ── ladder shape ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADungeonWithNoWavesIsRefused() => Rejects("no-waves", GoodItems, """
        {
          "dungeons": [
            {
              "id": "test-pit", "name": "The Test Pit",
              "entryFeeBonusSats": 250, "xpLevelCap": 10,
              "dropRequiresFullClear": true, "dropRoll": "DeterministicRng",
              "waves": [],
              "drops": [ { "itemId": "test-trinket", "weight": 1 } ]
            }
          ]
        }
        """);

    [Fact]
    public void WavesNumberedOutOfOrderAreRefused()
    {
        // The resolver walks 1..N. A gap or a repeat would silently resolve a wave with no offset and no
        // gear — a quietly easier ladder, which on a paying dungeon is a quietly cheaper drop.
        Rejects("non-contiguous-waves", GoodItems,
            GoodDungeons.Replace("{ \"wave\": 2, \"levelOffset\": 0,", "{ \"wave\": 7, \"levelOffset\": 0,"));
    }

    [Fact]
    public void NegativeWaveXpIsRefused()
        => Rejects("negative-wave-xp", GoodItems, GoodDungeons.Replace("\"xp\": 15", "\"xp\": -15"));

    // ── the JSON itself ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMisspelledStatFieldIsRefusedRatherThanIgnored()
    {
        // The quietest possible content bug: "atack" binds nothing, and a weapon ships with no attack at
        // all. Silently ignoring unknown fields is what makes that possible, so unknown fields are errors.
        Rejects("unknown-field", GoodItems.Replace("\"critPercent\": 1", "\"critPct\": 1"), GoodDungeons);
    }

    [Fact]
    public void AnUnknownEquipmentSlotIsRefused()
        => Rejects("unknown-slot", GoodItems.Replace("\"slot\": \"Trinket\"", "\"slot\": \"Hat\""), GoodDungeons);

    [Fact]
    public void AnUnknownCounterShapeIsRefused()
        => Rejects("unknown-counter-shape",
            GoodItems.Replace("\"mods\": { \"critPercent\": 1 }",
                              "\"mods\": { \"critPercent\": 1 }, \"counters\": \"Sideways\""), GoodDungeons);

    [Fact]
    public void AnUnknownDropRollModeIsRefused()
        => Rejects("unknown-drop-roll", GoodItems,
            GoodDungeons.Replace("\"dropRoll\": \"DeterministicRng\"", "\"dropRoll\": \"CoinFlip\""));

    [Fact]
    public void AMissingMoneyFieldIsRefusedRatherThanDefaulted()
    {
        // entryFeeBonusSats is the entry premium. Defaulting it to 0 would make a dungeon that forgot it
        // the cheapest farm in the game, so it is required.
        Rejects("missing-field", GoodItems, GoodDungeons.Replace("\"entryFeeBonusSats\": 250,", ""));
    }

    [Fact]
    public void AFractionalAuthoredNumberIsRefusedRatherThanTruncated()
        => Rejects("non-integer-number", GoodItems, GoodDungeons.Replace("\"weight\": 1", "\"weight\": 1.5"));

    [Fact]
    public void MalformedJsonIsRefused()
        => Rejects("json-malformed-items", GoodItems + "{{{", GoodDungeons);

    [Fact]
    public void ANegativeItemPriceIsRefused()
        => Rejects("negative-item-price", GoodItems.Replace("\"priceSats\": 100", "\"priceSats\": -100"), GoodDungeons);

    [Fact]
    public void AZeroMinLevelIsRefused()
        // The trailing comma keeps this off test-crown's "minLevel": 10.
        => Rejects("bad-item-min-level", GoodItems.Replace("\"minLevel\": 1,", "\"minLevel\": 0,"), GoodDungeons);

    // ── the shipped pack ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheShippedPackLoadsAndValidatesCleanly()
    {
        Assert.Empty(ContentValidation.Validate(ContentPack.Default, GameConfig.Default, ShippedSeals()));
        Assert.NotEmpty(ContentPack.Default.Items);
        Assert.NotEmpty(ContentPack.Default.Dungeons);
    }
}
