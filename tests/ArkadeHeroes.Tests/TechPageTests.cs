using System.Reflection;
using System.Text.RegularExpressions;
using ArkadeHeroes.Chain.Covenants;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The /tech page tells players which Arkade Script opcodes the game's covenants are built out of.
/// That is a claim about the code, and the page cannot reference the code (the WASM client's closure
/// excludes ArkadeHeroes.Chain), so the opcode table is written by hand.
///
/// <para>These tests read it back and check it against the private opcode constants in
/// <see cref="ArkadeCovenants"/>. A published opcode that no longer exists is a page lying about how
/// the game works — which, on a page whose entire argument is "you don't have to take our word for
/// it", is the worst kind of wrong.</para>
/// </summary>
public class TechPageTests
{
    private static readonly Lazy<string> Page = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "Pages", "Tech.razor");
        if (!File.Exists(path)) throw new InvalidOperationException($"Expected the tech page at {path}.");
        return File.ReadAllText(path);
    });

    /// <summary>Every (code, name) row the page publishes in its opcode table.</summary>
    private static (string Code, string Name)[] Published() =>
        Regex.Matches(Page.Value, """new\("(0x[0-9a-f]{2})",\s*"([A-Z]+)",""")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToArray();

    /// <summary>The opcode byte constants ArkadeCovenants actually emits, by their declared name.</summary>
    private static Dictionary<string, byte> CovenantOpcodes() =>
        typeof(ArkadeCovenants)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(byte) && f.Name.StartsWith("Op"))
            .ToDictionary(f => f.Name, f => (byte)f.GetRawConstantValue()!);

    [Fact]
    public void EveryOpcodeThePagePublishes_IsOneTheCovenantsActuallyUse()
    {
        var published = Published();
        Assert.NotEmpty(published);

        var real = CovenantOpcodes().Values.ToHashSet();
        foreach (var (code, name) in published)
        {
            var value = Convert.ToByte(code, 16);
            Assert.True(real.Contains(value),
                $"The tech page publishes {code} ({name}), but no opcode constant in ArkadeCovenants has "
                + "that value any more. Either the covenants changed and the page is now wrong, or the "
                + "row should be removed — do not leave players a table of opcodes we do not use.");
        }
    }

    [Fact]
    public void TheAssetIntrospectionOpcodes_AreAllPublished()
    {
        // These four are the ones that make a GAME possible rather than just a payment: counting assets
        // at an output, finding a group, summing it, reading its amount. If one silently stops being
        // published the page under-sells the only interesting part of the design.
        var published = Published().Select(p => p.Code).ToHashSet();
        var real = CovenantOpcodes();

        foreach (var name in new[]
                 {
                     "OpInspectNumAssetGroups", "OpFindAssetGroupByAssetId",
                     "OpInspectAssetGroup", "OpInspectAssetGroupSum", "OpInspectOutAssetCount",
                 })
        {
            Assert.True(real.ContainsKey(name), $"ArkadeCovenants no longer declares {name}.");
            Assert.Contains($"0x{real[name]:x2}", published);
        }
    }

    [Fact]
    public void ThePageDoesNotClaimAVerifiableRandomFunction()
    {
        // The randomness here is commit-reveal, not a VRF. They are not the same guarantee, and calling
        // one the other on a page about trust would be exactly the sort of overclaim the page argues
        // against. This test exists because that wording is an easy thing to drift into.
        Assert.DoesNotContain("VRF", Page.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit", Page.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePageSaysWhatIsNotCovenantEnforced()
    {
        // The honest-limits section is load-bearing. A page that lists only the guarantees reads as a
        // promise that everything is guaranteed.
        Assert.Contains("not covenant-enforced", Page.Value, StringComparison.OrdinalIgnoreCase);
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
