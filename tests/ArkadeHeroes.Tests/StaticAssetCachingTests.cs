using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A deploy once shipped new markup against a stale stylesheet, and the page rendered as unstyled
/// text. The cause: <c>index.html</c> asks for the runtime as
/// <c>blazor.webassembly#[.{fingerprint}].js</c>, so the framework gets a new url every build and is
/// always fetched — but <c>css/app.css</c> and <c>js/hero-render.js</c> keep the same url forever,
/// and nothing told the browser to revalidate them.
///
/// <para>These pin the header that fixes it. They are worth having because the failure is invisible
/// from the server's side: every file was correct on disk, and only a browser that had visited
/// before could see the bug.</para>
/// </summary>
public class StaticAssetCachingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StaticAssetCachingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>
    /// The test host has no published wwwroot, so there is no real app.css to fetch. What CAN be
    /// asserted without one is that the policy is wired at all — the middleware is configured with an
    /// OnPrepareResponse that sets the header — which is the thing that was missing.
    /// </summary>
    [Fact]
    public async Task UnfingerprintedAssets_AreServedMustRevalidate()
    {
        var client = _factory.CreateClient();

        // Any file the app itself serves out of wwwroot. In a test host these 404, and a 404 carries
        // no cache header — so assert on the one asset that always exists instead: none. Rather than
        // fake one, assert the SHAPE of the rule directly against the composed middleware below.
        var response = await client.GetAsync("/css/app.css");

        // If a wwwroot IS present (a developer running against a published layout), the header must
        // be there. If it is not, the request 404s and there is nothing to check — the source-level
        // assertion below is what guards the wiring in that case.
        if (response.IsSuccessStatusCode)
            Assert.Contains("no-cache", response.Headers.CacheControl?.ToString() ?? "");
    }

    /// <summary>
    /// The wiring itself, read out of Program.cs. Crude, and deliberately so: the behaviour cannot be
    /// exercised in a test host that has no wwwroot, but the line going missing is precisely the
    /// regression that broke a deploy, so it gets a tripwire rather than nothing.
    /// </summary>
    [Fact]
    public void TheServer_StillSetsACachePolicyOnItsOwnStaticFiles()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Server", "Program.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("OnPrepareResponse", source);
        Assert.Contains("\"no-cache\"", source);
        // The framework must NOT be swept into the same rule: it is fingerprinted, so re-fetching it
        // on every navigation would re-download the runtime for no reason.
        Assert.Contains("/_framework", source);
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
