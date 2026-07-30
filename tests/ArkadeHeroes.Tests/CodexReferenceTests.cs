using System.Text.RegularExpressions;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The /codex page publishes the game's actual numbers — rarity bands, affinity bonuses, sterility
/// risk, element multipliers. It cannot reference ArkadeHeroes.Core (the WASM client's dependency
/// closure excludes it), so those numbers are written into the page by hand.
///
/// <para>That is fine right up until someone retunes a band, at which point the page keeps confidently
/// telling players the old figure. These tests read the numbers back out of the page source and check
/// each against Core, so a retune fails the build instead of turning the reference into fiction.</para>
/// </summary>
public class CodexReferenceTests
{
    private static readonly Lazy<string> Page = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "Pages", "Codex.razor");
        if (!File.Exists(path)) throw new InvalidOperationException($"Expected the codex page at {path}.");
        return File.ReadAllText(path);
    });

    /// <summary>The one Band row the page prints for a tier, as (values, count, weight, affinity, sterile).</summary>
    private static (string Range, int Count, int Weight, double Affinity, int Sterile) Row(string tier)
    {
        var m = Regex.Match(
            Page.Value,
            $"""new\("{tier}",\s*"\w+",\s*"([^"]+)",\s*(\d+),\s*(\d+),\s*([\d.]+),\s*(\d+)\)""");
        Assert.True(m.Success, $"The codex page no longer publishes a '{tier}' row. If the tier is gone, "
                             + "drop the matching assertion; do not leave this test checking nothing.");
        return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups[5].Value));
    }

    private static double Constant(string name)
    {
        var m = Regex.Match(Page.Value, $@"{name}\s*=\s*([\d.]+)");
        Assert.True(m.Success, $"The codex page no longer defines {name}.");
        return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheRarityBands_MatchTheOnesTheGameGrades_By()
    {
        var b = GameConfig.Default.Rarity;

        // Each published range must be exactly the span of values Traits.TierOf actually assigns.
        Assert.Equal("255", Row("Legendary").Range);
        Assert.Equal($"{b.EpicCutoff}–{b.LegendaryCutoff - 1}", Row("Epic").Range);
        Assert.Equal($"{b.RareCutoff}–{b.EpicCutoff - 1}", Row("Rare").Range);
        Assert.Equal($"{b.UncommonCutoff}–{b.RareCutoff - 1}", Row("Uncommon").Range);
        Assert.Equal($"0–{b.UncommonCutoff - 1}", Row("Common").Range);
    }

    [Fact]
    public void TheBandSizes_AddUpToEveryPossibleValue()
    {
        var published = new[] { "Legendary", "Epic", "Rare", "Uncommon", "Common" }
            .ToDictionary(t => t, t => Row(t).Count);

        // Counted straight off TierOf rather than from the cutoffs, so a change to how a value is
        // graded — not just to a cutoff number — is caught too.
        var actual = Enumerable.Range(0, 256)
            .GroupBy(v => Traits.TierOf((byte)v).ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (tier, count) in published)
            Assert.Equal(actual[tier], count);
        Assert.Equal(256, published.Values.Sum());
    }

    [Fact]
    public void TheRarityScores_MatchWhatTheBoardsRankBy()
    {
        var b = GameConfig.Default.Rarity;
        Assert.Equal(b.LegendaryWeight, Row("Legendary").Weight);
        Assert.Equal(b.EpicWeight, Row("Epic").Weight);
        Assert.Equal(b.RareWeight, Row("Rare").Weight);
        Assert.Equal(b.UncommonWeight, Row("Uncommon").Weight);
        Assert.Equal(b.CommonWeight, Row("Common").Weight);
    }

    [Fact]
    public void TheAffinityBonuses_AndTheirCap_MatchWhatCombatApplies()
    {
        var a = GameConfig.Default.Affinity;
        // The page prints percentages; the config holds fractions.
        Assert.Equal(a.Legendary * 100, Row("Legendary").Affinity, 3);
        Assert.Equal(a.Epic * 100, Row("Epic").Affinity, 3);
        Assert.Equal(a.Rare * 100, Row("Rare").Affinity, 3);
        Assert.Equal(a.Uncommon * 100, Row("Uncommon").Affinity, 3);
        Assert.Equal(a.Common * 100, Row("Common").Affinity, 3);
        Assert.Equal(a.Cap * 100, Constant("AffinityCapPct"), 3);
    }

    [Fact]
    public void TheSterilityRisks_MatchWhatBirthActuallyRolls()
    {
        var s = GameConfig.Default.Sterility;
        Assert.Equal(s.Legendary, Row("Legendary").Sterile);
        Assert.Equal(s.Epic, Row("Epic").Sterile);
        Assert.Equal(s.Rare, Row("Rare").Sterile);
        Assert.Equal(s.Uncommon, Row("Uncommon").Sterile);
        Assert.Equal(0, Row("Common").Sterile);   // Common — including every gen-0 starter — is always fertile
    }

    [Fact]
    public void TheCombatMultipliers_MatchTheEngine()
    {
        var c = GameConfig.Default.Combat;
        Assert.Equal(c.ElementStrong, Constant("ElementStrong"), 3);
        Assert.Equal(c.ElementWeak, Constant("ElementWeak"), 3);
        Assert.Equal(c.CritMultiplier, Constant("CritMultiplier"), 3);
        Assert.Equal(c.GeneSkillBLevel, (int)Constant("GeneSkillBLevel"));
        Assert.Equal(c.BurstLevel, (int)Constant("BurstLevel"));
    }

    [Fact]
    public void TheElementRing_IsTheRealOne_InTheRealOrder()
    {
        var published = Regex.Match(Page.Value, @"Elements\s*=\s*\r?\n?\s*\[([^\]]+)\]").Groups[1].Value;
        var names = Regex.Matches(published, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();

        Assert.Equal(Enum.GetNames<Element>(), names);

        // The page claims each element beats the NEXT one in the ring. Check that against the matrix
        // itself rather than trusting the ordering to imply it.
        for (var i = 0; i < names.Length; i++)
        {
            var self = (Element)i;
            var next = (Element)((i + 1) % names.Length);
            var prev = (Element)((i + names.Length - 1) % names.Length);
            Assert.True(ElementMatrix.Multiplier(self, next) > 1.0, $"{self} should beat {next}");
            Assert.True(ElementMatrix.Multiplier(self, prev) < 1.0, $"{self} should lose to {prev}");
        }
    }

    [Fact]
    public void TheEightTraits_AreAllPublished_AndTheAffinitiesAreMarkedAsSuch()
    {
        var rows = Regex.Matches(Page.Value, @"new\(""([^""]+)"",\s*""[^""]*?\."",\s*(true|false)\)")
            .Select(m => (Name: m.Groups[1].Value, IsAffinity: m.Groups[2].Value == "true"))
            .ToArray();

        Assert.Equal(Traits.CategoryCount, rows.Length);
        // Exactly the categories Traits.IsAffinity singles out must be the ones flagged on the page —
        // they are the two that move damage, so mislabelling one misleads about a fight.
        var flagged = rows.Count(r => r.IsAffinity);
        var actual = Enum.GetValues<TraitCategory>().Count(Traits.IsAffinity);
        Assert.Equal(actual, flagged);
        Assert.True(rows[^1].IsAffinity && rows[^2].IsAffinity,
            "The affinities are the last two categories in the genome and should be published last.");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }
}
