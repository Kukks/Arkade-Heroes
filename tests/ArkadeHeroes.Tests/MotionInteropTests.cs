using System.Text.RegularExpressions;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The dungeon crawls ask the browser whether this player wants motion suppressed, which means C# calling
/// JavaScript by name across a gap no compiler inspects. Rename the export and nothing fails to build — the
/// call throws at runtime, the wrapper swallows it as "motion is fine", and a player who asked for reduced
/// motion silently gets the full cinematic instead. That is exactly the kind of break nobody reports.
///
/// <para>Not a test of matchMedia (that needs a real browser), a test of the seam we own.</para>
/// </summary>
public class MotionInteropTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string Module() =>
        Read("src", "ArkadeHeroes.Web", "wwwroot", "js", "motion.js");

    private static string Wrapper() =>
        Read("src", "ArkadeHeroes.Web", "Components", "Motion.cs");

    /// <summary>Every name the C# side invokes must be exported by the module it imports.</summary>
    [Fact]
    public void EveryFunctionTheWrapperCalls_IsExportedByTheModule()
    {
        var invoked = Regex.Matches(Wrapper(), @"InvokeAsync<[^>]+>\(""(?<name>[A-Za-z0-9_]+)""")
            .Select(m => m.Groups["name"].Value)
            .Where(n => n != "import")   // the module import itself, not a module function
            .Distinct()
            .ToList();

        Assert.NotEmpty(invoked);   // a wrapper that calls nothing would pass this vacuously

        var exported = Regex.Matches(Module(), @"export\s+(?:async\s+)?function\s+(?<name>[A-Za-z0-9_]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet();

        foreach (var name in invoked)
            Assert.True(exported.Contains(name),
                $"Motion calls '{name}', but motion.js exports only: {string.Join(", ", exported.OrderBy(x => x))}");
    }

    /// <summary>A moved or renamed module file is the same runtime-only breakage as a renamed export.</summary>
    [Fact]
    public void TheModulePathTheWrapperImports_Exists()
    {
        var import = Regex.Match(Wrapper(), @"""import"",\s*""(?<path>[^""]+)""");
        Assert.True(import.Success, "Expected Motion to import its JS module by path.");

        // "./js/motion.js" is relative to wwwroot at runtime.
        var relative = import.Groups["path"].Value.TrimStart('.', '/');
        Assert.True(
            File.Exists(Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "wwwroot",
                relative.Replace('/', Path.DirectorySeparatorChar))),
            $"Motion imports '{import.Groups["path"].Value}', which does not exist under wwwroot.");
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
