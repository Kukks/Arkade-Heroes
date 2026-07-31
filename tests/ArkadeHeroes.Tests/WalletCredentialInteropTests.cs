using System.Text.RegularExpressions;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The wallet's recovery phrase can be handed to the browser's credential manager, which means C# calling
/// JavaScript by name across a gap no compiler inspects. Rename an export and nothing fails to build — it
/// fails at runtime, on the button that is supposed to be protecting someone's keys, in the one browser
/// family that supports the feature at all.
///
/// <para>These read both sides and check they still agree. Not a test of the Credential Management API
/// (that needs a real Chromium), a test of the seam we own.</para>
/// </summary>
public class WalletCredentialInteropTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string Module() =>
        Read("src", "ArkadeHeroes.Web", "wwwroot", "js", "wallet-credentials.js");

    private static string Wrapper() =>
        Read("src", "ArkadeHeroes.Web", "Wallet", "WalletCredentialStore.cs");

    /// <summary>Every name the C# side invokes must be exported by the module it imports.</summary>
    [Fact]
    public void EveryFunctionTheWrapperCalls_IsExportedByTheModule()
    {
        var invoked = Regex.Matches(Wrapper(), @"InvokeAsync<[^>]+>\(""(?<name>[A-Za-z0-9_]+)""")
            .Select(m => m.Groups["name"].Value)
            .Where(n => n != "import")   // the module import itself, not a module function
            .Distinct()
            .ToList();

        Assert.NotEmpty(invoked);   // a wrapper that calls nothing would pass every check below vacuously

        var exported = Regex.Matches(Module(), @"export\s+(?:async\s+)?function\s+(?<name>[A-Za-z0-9_]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet();

        foreach (var name in invoked)
            Assert.True(exported.Contains(name),
                $"WalletCredentialStore calls '{name}', but wallet-credentials.js exports only: {string.Join(", ", exported.OrderBy(x => x))}");
    }

    /// <summary>
    /// The wrapper imports the module by path. A moved or renamed file is the same runtime-only breakage as
    /// a renamed export, so pin that the path it asks for is the path that exists.
    /// </summary>
    [Fact]
    public void TheModulePathTheWrapperImports_Exists()
    {
        var import = Regex.Match(Wrapper(), @"""import"",\s*""(?<path>[^""]+)""");
        Assert.True(import.Success, "Expected WalletCredentialStore to import its JS module by path.");

        // "./js/wallet-credentials.js" is relative to wwwroot at runtime.
        var relative = import.Groups["path"].Value.TrimStart('.', '/');
        Assert.True(File.Exists(Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "wwwroot", relative.Replace('/', Path.DirectorySeparatorChar))),
            $"WalletCredentialStore imports '{import.Groups["path"].Value}', which does not exist under wwwroot.");
    }

    /// <summary>
    /// The feature is Chromium-and-https only. Without the guard the buttons would throw on Firefox and
    /// Safari instead of simply not appearing, so the module must actually test for the API rather than
    /// assume it — and every entry point has to consult that guard, not just the one that reports support.
    /// </summary>
    [Fact]
    public void TheModuleFeatureDetects_AndEveryEntryPointHonoursIt()
    {
        var js = Module();
        Assert.Contains("isSecureContext", js);
        Assert.Contains("PasswordCredential", js);

        // save() and load() must each bail via isSupported() before touching navigator.credentials.
        foreach (var fn in new[] { "save", "load" })
        {
            var body = Regex.Match(js, $@"export\s+async\s+function\s+{fn}\b.*?\n\}}", RegexOptions.Singleline);
            Assert.True(body.Success, $"Expected an exported async '{fn}' in wallet-credentials.js.");
            Assert.Contains("isSupported()", body.Value);
        }
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
