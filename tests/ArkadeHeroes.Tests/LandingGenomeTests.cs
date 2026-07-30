using System.Text.RegularExpressions;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The landing page states outright that its breeding, fusion and death-match examples are real
/// outputs of the game's own functions rather than an artist's impression. Nothing enforced that:
/// ArkadeHeroes.Web cannot reference ArkadeHeroes.Core (it is a WASM client whose dependency
/// closure deliberately excludes it), so the genomes are pasted constants, and a later retune of
/// the mixer thresholds, the fusion concentrate rule or the absorb odds would leave the page
/// quietly asserting something false.
///
/// These tests re-derive every published constant from Core and fail if it drifts. Reading the
/// source file is the point — it binds the assertion to the literal text a visitor is told to
/// trust, not to a copy in the test.
/// </summary>
public class LandingGenomeTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Published = new(Parse);

    private static string Const(string name) =>
        Published.Value.TryGetValue(name, out var v)
            ? v
            : throw new InvalidOperationException(
                $"LandingGenomes.{name} is gone. If the landing page no longer publishes it, drop the "
                + $"matching assertion here; do not delete the constant and leave this test asserting nothing.");

    private static Genome G(string name) => Genome.FromHex(Const(name));
    private static byte[] E(string name) => Convert.FromHexString(Const(name));

    [Fact]
    public void TheChildOnTheLandingPage_IsWhatGeneMixerActuallyProduces()
    {
        var child = GeneMixer.Mix(G("Ember"), G("Tide"), E("Entropy"));
        Assert.Equal(Const("Child"), child.ToHex());
    }

    [Fact]
    public void TheFusedHeroOnTheLandingPage_IsWhatFusionActuallyProduces()
    {
        var fused = Fusion.Fuse(G("Ember"), G("Tide"), E("Entropy"));
        Assert.Equal(Const("Fused"), fused.ToHex());
    }

    [Fact]
    public void TheAbsorbedWinnerOnTheLandingPage_IsWhatAbsorbActuallyProduces()
    {
        var outcome = Absorb.Resolve(G("Tide"), G("Ember"), E("AbsorbEntropy"), AbsorbOdds.Default);
        Assert.Equal(Const("Absorbed"), outcome.Result.ToHex());
    }

    /// <summary>
    /// The breeding panel does not just show a child — it labels where each of its eight traits came
    /// from, and claims the set covers all three inheritance rules. That claim is the part a reader
    /// actually learns from, so it is pinned separately from the hex.
    /// </summary>
    [Fact]
    public void TheChild_StillDemonstratesAllThreeInheritanceRules()
    {
        Genome a = G("Ember"), b = G("Tide"), child = GeneMixer.Mix(G("Ember"), G("Tide"), E("Entropy"));

        int fromAParent = 0, surfaced = 0, mutated = 0;
        foreach (var cat in Enum.GetValues<TraitCategory>())
        {
            var dom = child.DominantGene(cat);
            if (dom == a.DominantGene(cat) || dom == b.DominantGene(cat)) fromAParent++;
            else if (dom == a.RecessiveGene(cat) || dom == b.RecessiveGene(cat)) surfaced++;
            else mutated++;
        }

        // The page says: six straight from a parent, one recessive surfaced, one fresh mutation.
        Assert.Equal(6, fromAParent);
        Assert.Equal(1, surfaced);
        Assert.Equal(1, mutated);
        Assert.Equal(Traits.CategoryCount, fromAParent + surfaced + mutated);
    }

    /// <summary>
    /// The absorb panel claims exactly one byte moved, and that it was a genuine upgrade. Both are
    /// load-bearing: "only upgrades are on the table" is the rule that stops a death match from
    /// being a coin flip on your hero's identity.
    /// </summary>
    [Fact]
    public void TheAbsorb_MovesExactlyOneByte_AndOnlyUpward()
    {
        Genome winner = G("Tide"), loser = G("Ember");
        var outcome = Absorb.Resolve(winner, loser, E("AbsorbEntropy"), AbsorbOdds.Default);

        var changed = Enumerable.Range(0, Genome.Size)
            .Where(i => outcome.Result[i] != winner[i])
            .ToArray();

        Assert.Single(changed);
        var cat = (TraitCategory)((changed[0] - 16) / 2);
        Assert.Equal(TraitCategory.Temperament, cat);
        Assert.True(
            Traits.WeightOf(loser.DominantGene(cat)) > Traits.WeightOf(winner.DominantGene(cat)),
            "The absorbed trait must be strictly rarer than what the winner already had.");
        Assert.Equal(RarityTier.Rare, Traits.TierOf(outcome.Result.DominantGene(cat)));
    }

    /// <summary>Fusion takes stats from the base alone — the page says fusing never inflates power.</summary>
    [Fact]
    public void TheFusion_LeavesTheMechanicalBytesOfTheBaseUntouched()
    {
        var fused = Fusion.Fuse(G("Ember"), G("Tide"), E("Entropy"));
        Assert.Equal(Convert.ToHexString(G("Ember").Bytes[..16]), Convert.ToHexString(fused.Bytes[..16]));
    }

    // --- Reading the published constants -----------------------------------------------------

    private static IReadOnlyDictionary<string, string> Parse()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "LandingGenomes.cs");
        if (!File.Exists(path))
            throw new InvalidOperationException($"Expected the landing genomes at {path}.");

        var found = Regex.Matches(File.ReadAllText(path), """public const string (\w+) = "([0-9a-f]{64})";""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        // A typo in the constant name or a reformat that breaks the pattern would otherwise turn every
        // assertion below into a vacuous pass against an empty dictionary.
        if (found.Count == 0)
            throw new InvalidOperationException($"Parsed no 64-char genome constants out of {path}.");
        return found;
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
