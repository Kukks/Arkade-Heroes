using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A deploy once shipped new markup against a stale stylesheet, and the page rendered as unstyled
/// text. The cause: <c>index.html</c> asks for the runtime as
/// <c>blazor.webassembly#[.{fingerprint}].js</c>, so the framework gets a new url every build and is
/// always fetched — but <c>css/app.css</c> and <c>js/hero-render.js</c> keep the same url forever,
/// and nothing told the browser to revalidate them.
///
/// <para>The test host normally has no published wwwroot, so there is nothing to fetch and nothing to
/// assert. These build one — a temp web root holding a real stylesheet — so the actual middleware
/// answers actual requests, and the fix is verified rather than merely present in the source.</para>
/// </summary>
public class StaticAssetCachingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StaticAssetCachingTests(WebApplicationFactory<Program> factory) => _factory = factory;



    /// <summary>A throwaway web root with one ordinary asset and one framework asset.</summary>
    private static string NewWebRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arkade-static-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "css"));
        Directory.CreateDirectory(Path.Combine(root, "_framework"));
        File.WriteAllText(Path.Combine(root, "css", "app.css"), "/* stand-in for the real stylesheet */");
        File.WriteAllText(Path.Combine(root, "_framework", "probe.txt"), "stand-in for a fingerprinted asset");
        return root;
    }

    [Fact]
    public async Task AnOrdinaryAsset_MustBeRevalidated_AndTheFrameworkIsNotOursToPolice()
    {
        var root = NewWebRoot();
        try
        {
            using var factory = _factory.WithWebHostBuilder(b => b.UseWebRoot(root));
            var client = factory.CreateClient();

            // css/app.css keeps the same url across every deploy, so the browser has to be told to check.
            var ordinary = await client.GetAsync("/css/app.css");
            Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
            Assert.Contains("no-cache", ordinary.Headers.CacheControl?.ToString() ?? "",
                StringComparison.OrdinalIgnoreCase);

            // _framework is NOT ours to police. UseBlazorFrameworkFiles serves it before this
            // middleware is reached — established by setting a probe header unconditionally in our
            // OnPrepareResponse and finding it absent from the response below — and it applies its own
            // policy, which is also no-cache and correctly so: blazor.boot.json has a fixed url and has
            // to be revalidated for a new build to be discovered at all.
            //
            // So the assertion here is deliberately about REACHABILITY, not about the header: the
            // framework must still be served, and our rule must not be what decides it. Asserting a
            // value we do not own would be a test of Blazor's defaults dressed up as a test of ours.
            var framework = await client.GetAsync("/_framework/probe.txt");
            Assert.Equal(HttpStatusCode.OK, framework.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The header has to survive a conditional request too: revalidation is only useful if the second
    /// request comes back 304 rather than re-sending the bytes, and only correct if the policy is still
    /// attached when it does.
    /// </summary>
    [Fact]
    public async Task RevalidatingAnUnchangedAsset_Returns304_AndKeepsThePolicy()
    {
        var root = NewWebRoot();
        try
        {
            using var factory = _factory.WithWebHostBuilder(b => b.UseWebRoot(root));
            var client = factory.CreateClient();

            var first = await client.GetAsync("/css/app.css");
            var etag = first.Headers.ETag;
            Assert.NotNull(etag);

            var conditional = new HttpRequestMessage(HttpMethod.Get, "/css/app.css");
            conditional.Headers.IfNoneMatch.Add(etag!);
            var second = await client.SendAsync(conditional);

            Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
            Assert.Contains("no-cache", second.Headers.CacheControl?.ToString() ?? "",
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
