using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// Publishes the WASM frontend, serves the resulting bundle, and opens a real browser at it.
///
/// <para>The publish is the point. Blazor's Release publish is a different artifact from anything a build
/// or a unit test sees — IL-linked, fingerprinted, brotli-compressed, with a boot manifest — and defects
/// that exist only there have reached players before. Nothing below may substitute a build output for it.</para>
///
/// <para>Shared across the whole suite: publishing costs minutes, and every test here wants the same
/// bundle in the same browser.</para>
/// </summary>
public sealed class PublishedAppFixture : IAsyncLifetime
{
    /// <summary>Port 5198 by local convention — 5199 and the framework defaults belong to other agents
    /// and to `dotnet run`, and a browser suite that fights a dev server for a port fails confusingly.</summary>
    private const int Port = 5198;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Reuse an already-published bundle when the caller points at one (CI publishes once for the
    /// job). Otherwise publish on demand, so a bare `dotnet test` still works.</summary>
    private const string PublishDirVariable = "ARKADE_WEB_PUBLISH_DIR";

    private IHost? _host;
    private IPlaywright? _playwright;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var wwwroot = ResolvePublishedWwwroot();

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web => web
                .UseUrls(BaseUrl)
                .Configure(app =>
                {
                    var files = new PhysicalFileProvider(wwwroot);
                    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = files,
                        // The published bundle contains extensionless and .dat/.wasm assets the default
                        // content-type map does not know. Serving them as octet-stream is what a real host
                        // does; refusing to serve them would fail the boot for a reason no player would hit.
                        ServeUnknownFileTypes = true,
                        DefaultContentType = "application/octet-stream",
                    });
                    // SPA fallback: deep links like /gauntlet are routes inside the bundle, not files.
                    app.Run(async context =>
                    {
                        context.Response.ContentType = "text/html";
                        await context.Response.SendFileAsync(Path.Combine(wwwroot, "index.html"));
                    });
                }))
            .Build();

        await _host.StartAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static string ResolvePublishedWwwroot()
    {
        var repoRoot = FindRepoRoot();

        if (Environment.GetEnvironmentVariable(PublishDirVariable) is { Length: > 0 } supplied)
        {
            // Resolved against the REPOSITORY ROOT when relative, not against the working directory: the
            // test host runs from its own output folder, so `-o published-web` next to the solution — the
            // obvious thing to write, and what CI wrote — pointed at nothing and every test failed on a
            // missing wwwroot. An absolute value is returned untouched.
            var root = Path.IsPathRooted(supplied) ? supplied : Path.Combine(repoRoot, supplied);
            var given = Path.Combine(root, "wwwroot");
            if (!Directory.Exists(given))
                throw new DirectoryNotFoundException(
                    $"{PublishDirVariable} is set to '{supplied}' (resolved to '{root}') but there is no " +
                    "wwwroot in it. Publish the frontend there first: " +
                    $"dotnet publish src/ArkadeHeroes.Web -c Release -o {supplied}");
            return given;
        }

        var output = Path.Combine(Path.GetTempPath(), "arkade-heroes-browser-suite-publish");

        var publish = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                "publish", Path.Combine(repoRoot, "src", "ArkadeHeroes.Web"),
                "-c", "Release", "-o", output,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("could not start dotnet publish");

        publish.WaitForExit();
        if (publish.ExitCode != 0)
            throw new InvalidOperationException(
                $"publishing the frontend failed:\n{publish.StandardOutput.ReadToEnd()}\n{publish.StandardError.ReadToEnd()}");

        return Path.Combine(output, "wwwroot");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ArkadeHeroes.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }
}

[CollectionDefinition(Name)]
public sealed class PublishedAppCollection : ICollectionFixture<PublishedAppFixture>
{
    public const string Name = "published-app";
}
