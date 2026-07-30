using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The server hosts the Blazor bundle, so ONE container is the whole deployment — and the
/// SPA fallback that makes deep links work must never answer for the API.
///
/// A bare <c>MapFallbackToFile("index.html")</c> catches every unmatched request, /api
/// included. That is invisible in most test hosts because they have no wwwroot, so there is
/// no index.html to serve and everything 404s anyway; it only appears once a real bundle is
/// present, i.e. in the deployed image and nowhere else. These tests therefore supply a REAL
/// index.html, because without one they would pass no matter what the fallback did.
///
/// The failure it guards against is quiet rather than loud: every client here parses JSON
/// and switches on the status code, so an API route answering 200 with a page of HTML shows
/// up as a parse error far from the cause, and a deleted or mistyped route looks healthy in
/// a browser.
/// </summary>
public class SpaFallbackTests : IDisposable
{
    private readonly string _webRoot;

    public SpaFallbackTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), $"ah-webroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_webRoot);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html>SPA SHELL</html>");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseWebRoot(_webRoot));

    [Fact]
    public async Task AnUnknownApiRoute_Is404_AndNotTheSpaShell()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/definitely-not-a-route");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
        // The status alone is not enough: a 404 whose body is the shell would still break a
        // client that reads the body before the code.
        Assert.DoesNotContain("SPA SHELL", body);
    }

    [Fact]
    public async Task AClientSideDeepLink_GetsTheSpaShell()
    {
        // The other half — a fallback that refuses everything would pass the test above while
        // breaking every bookmarked hero page.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/heroes/some-deep-link");

        res.EnsureSuccessStatusCode();
        Assert.Contains("SPA SHELL", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARealApiRoute_StillAnswersNormally()
    {
        // Proves the fallback did not shadow the API it sits beside. /api/chain/info is the
        // same endpoint the container health check uses.
        using var factory = Factory();
        using var client = factory.CreateClient();

        var res = await client.GetAsync("/api/chain/info");

        res.EnsureSuccessStatusCode();
        Assert.DoesNotContain("SPA SHELL", await res.Content.ReadAsStringAsync());
    }
}
