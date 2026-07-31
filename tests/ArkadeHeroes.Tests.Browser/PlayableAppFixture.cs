using System.Diagnostics;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace ArkadeHeroes.Tests.Browser;

/// <summary>
/// The published bundle, served by the REAL game server, driven by a real browser.
///
/// <para><see cref="PublishedAppFixture"/> proves the app starts. It cannot prove the app WORKS, because it
/// serves the bundle off a bare static-file host with every backend aborted — so every page it visits is a
/// page whose data never arrived. A page that renders an error state perfectly is indistinguishable from a
/// page that renders the truth, and both pass.</para>
///
/// <para>This fixture removes that blind spot by standing up <c>ArkadeHeroes.Server</c> itself — its own
/// <c>Program</c>, its own routes, its own middleware — on Kestrel, with the published bundle as its
/// wwwroot. That is not a test rig approximating production; it IS the shipped topology. The container
/// copies the bundle into wwwroot precisely so one image is the whole deployment, which makes the browser's
/// origin equal to the API's. Same origin here, same origin there.</para>
///
/// <para>The chain underneath is the in-memory simulator, the same one the ~993-test server suite runs on.
/// So the API answers with real game state — real heroes, real prices, real listings — while needing no
/// Docker, no arkd and no bitcoind. A test can seed that state through <see cref="Api"/> (the typed SDK)
/// and then assert the BROWSER draws it.</para>
///
/// <para>What this deliberately does NOT reach: the in-browser wallet. It talks to arkd directly, so
/// creating one, funding it or spending from it needs a live node — see the class remarks on
/// <c>NewPlayerWalkTests</c> for exactly where the wall is and what is on the far side of it.</para>
/// </summary>
public sealed class PlayableAppFixture : IAsyncLifetime
{
    /// <summary>Reuse an already-published bundle when the caller points at one (CI publishes once for the
    /// job, and both fixtures in this project read the same variable). Otherwise publish on demand.</summary>
    private const string PublishDirVariable = "ARKADE_WEB_PUBLISH_DIR";

    /// <summary>The in-browser wallet's arkd and esplora endpoints on the regtest profile, taken from
    /// NArk's own <c>ArkNetworkConfig.Regtest</c>. Named here so the browser context can refuse them
    /// DETERMINISTICALLY: on a developer machine with the regtest stack up they would answer, and a suite
    /// whose results depend on what happens to be listening is not a gate.</summary>
    internal static readonly string[] WalletBackends = ["http://localhost:7070", "http://localhost:3000"];

    /// <summary>
    /// The breeding fee this server runs on — deliberately NOT the shipped default of 1,000, so that a
    /// price on screen matching it is evidence the browser read it from this server rather than from a
    /// constant compiled into the bundle. Every other fee in the game is derived from this one.
    /// </summary>
    public const long BreedingFeeSats = 1_337;

    private KestrelServerFactory? _factory;
    private IPlaywright? _playwright;
    private string? _staged;

    /// <summary>Where the browser loads the app from — and, being the same origin, where its API calls go.</summary>
    public string BaseUrl { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// A typed SDK client against the same server the browser is using, for SEEDING. Tests use it to put
    /// real state in front of the browser (a hero that exists, a listing at a real price) so the assertion
    /// is "the page shows what the server holds" rather than "the page shows something".
    ///
    /// <para>A NEW client every time, deliberately. The SDK holds the player's bearer token on the client
    /// after register/login, so a shared one would quietly re-authenticate every seeded player as whoever
    /// registered last — and a test seeding two players to look at each other's heroes would be seeding
    /// one. Callers hold the client they were given for as long as they need that identity.</para>
    /// </summary>
    public ArkadeHeroesClient Api => new(new HttpClient { BaseAddress = new Uri(BaseUrl) });

    public async Task InitializeAsync()
    {
        _staged = StageBundleForSameOriginHosting(ResolvePublishedWwwroot());

        _factory = new KestrelServerFactory(_staged);
        BaseUrl = _factory.StartAndGetAddress();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        _factory?.Dispose();
        if (_staged is not null && Directory.Exists(_staged))
            try { Directory.Delete(_staged, recursive: true); } catch (IOException) { /* temp dir; leave it */ }
    }

    /// <summary>
    /// A fresh browser context (its own storage, so its own wallet DB and its own terms answer) with the
    /// player's Ark node held unreachable, and every console error and page error collected.
    ///
    /// <para>A page that draws correctly while throwing in the console is a defect, not a pass — that is
    /// exactly the shape of the startup crash this project was built to notice — so the errors come back
    /// with the page rather than being left for a caller to remember to ask for.</para>
    /// </summary>
    /// <param name="breakApi">
    /// An optional glob for game-API calls this page should find BROKEN — served a 503, as an arena that is
    /// up enough to answer but not up enough to help would. Used to tell an empty page apart from a page
    /// whose read failed, which from the outside look identical and mean opposite things.
    /// </param>
    public async Task<PageSession> OpenAsync(string path, string? breakApi = null)
    {
        var context = await Browser.NewContextAsync();

        // The wallet's own backends, refused at the network layer. NOT the game API — that is the whole
        // point of this fixture and it is same-origin, so it is never matched here.
        await context.RouteAsync("**", async route =>
        {
            var url = route.Request.Url;
            if (WalletBackends.Any(b => url.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });

        if (breakApi is not null)
            await context.RouteAsync(breakApi, route => route.FulfillAsync(new()
            {
                Status = 503,
                ContentType = "application/json",
                Body = "{\"error\":\"the arena is briefly unavailable\"}",
            }));

        var page = await context.NewPageAsync();
        var session = new PageSession(page, BaseUrl);
        page.PageError += (_, e) => session.Errors.Add($"pageerror: {e}");
        // The LOCATION matters as much as the text. Chromium reports a refused request as a bare
        // "Failed to load resource: net::ERR_FAILED" with the url only in the location, so a message
        // recorded without it cannot be attributed to anything and has to be either ignored wholesale
        // or treated as a failure — both wrong.
        page.Console += (_, m) =>
        {
            if (m.Type == "error") session.Errors.Add($"console.error [{m.Location}]: {m.Text}");
        };

        await page.GotoAsync($"{BaseUrl}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await session.WaitForAppAsync();
        return session;
    }

    /// <summary>
    /// Copies the published bundle and rewrites its <c>appsettings.json</c> exactly the way
    /// <c>src/ArkadeHeroes.Server/docker-entrypoint.sh</c> does at container start: <c>ArkNetwork</c> only,
    /// with <c>ApiBaseUrl</c> dropped so the app resolves its API against the origin it was served from.
    ///
    /// <para>This is not a test-only shim. It is the shipped mechanism, reproduced — including the part the
    /// entrypoint learned the hard way, that the pre-compressed <c>.gz</c>/<c>.br</c> siblings the publish
    /// generated must be removed or the browser keeps reading the value baked in at publish time while
    /// curl shows the new one.</para>
    ///
    /// <para>Copied rather than rewritten in place because CI publishes ONCE and
    /// <see cref="PublishedAppFixture"/> reads the same directory; mutating a shared input would make one
    /// suite's behaviour depend on whether the other had run.</para>
    /// </summary>
    private static string StageBundleForSameOriginHosting(string publishedWwwroot)
    {
        var staged = Path.Combine(Path.GetTempPath(), $"arkade-heroes-playable-{Guid.NewGuid():N}");
        CopyDirectory(new DirectoryInfo(publishedWwwroot), new DirectoryInfo(staged));

        var settings = Path.Combine(staged, "appsettings.json");
        File.WriteAllText(settings, "{\n  \"ArkNetwork\": \"regtest\"\n}\n");
        foreach (var precompressed in new[] { settings + ".gz", settings + ".br" })
            if (File.Exists(precompressed)) File.Delete(precompressed);

        return staged;
    }

    private static void CopyDirectory(DirectoryInfo source, DirectoryInfo target)
    {
        target.Create();
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name), overwrite: true);
        foreach (var dir in source.GetDirectories())
            CopyDirectory(dir, new DirectoryInfo(Path.Combine(target.FullName, dir.Name)));
    }

    private static string ResolvePublishedWwwroot()
    {
        var repoRoot = FindRepoRoot();

        if (Environment.GetEnvironmentVariable(PublishDirVariable) is { Length: > 0 } supplied)
        {
            // Resolved against the REPOSITORY ROOT when relative, not the working directory: the test host
            // runs from its own output folder. Same rule as PublishedAppFixture, for the same reason.
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

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> pinned to a REAL Kestrel socket instead of the
    /// in-memory TestServer, because a browser cannot dial an in-memory transport.
    ///
    /// <para>The double-build below is the documented shape for this (dotnet/aspnetcore#33846): the base
    /// class insists on being handed a TestServer-backed host, so one host is built for it and a second,
    /// Kestrel-backed one is built and started for the browser to actually talk to. The Kestrel host must
    /// start FIRST — with minimal hosting's deferred builder, the address it bound is not readable until
    /// it has.</para>
    /// </summary>
    private sealed class KestrelServerFactory(string webRoot) : WebApplicationFactory<Program>
    {
        private IHost? _kestrel;

        public string StartAndGetAddress()
        {
            // Touching the property is what triggers CreateHost. Cheap, and it keeps the address read below
            // from depending on a test having made a request first.
            _ = Services;
            var addresses = _kestrel!.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            // 127.0.0.1 rather than localhost: on a dual-stack machine "localhost" can resolve to ::1 while
            // Kestrel bound the v4 loopback, and the browser then reports a bare connection refusal.
            return addresses.First().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureWebHost(web => web
                // The published bundle. UseBlazorFrameworkFiles + the SPA-fallback middleware in Program.cs
                // both key off this, so setting it is what turns an API-only host into the real deployment.
                .UseWebRoot(webRoot)
                // Explicit rather than inherited. The in-memory chain is this suite's whole premise, and
                // Program.cs defaults to it — but a default is not a guarantee, and a host that silently
                // picked up NArk mode would fail as a mystery instead of as a configuration error.
                .UseSetting("Chain:Mode", "InMemory")
                // Overrides `"Urls": "http://localhost:5210"` in the server's OWN appsettings.json, which
                // both hosts below inherit. Left alone, a developer running the API on its documented dev
                // port takes the entire suite down with an address-in-use that names neither the port nor
                // the file it came from. Port 0 also means two of these can run at once.
                .UseSetting("Urls", "http://127.0.0.1:0")
                // A fee that is NOT the default, and not a round number anyone would reach for.
                //
                // This is what makes a price assertion mean something. Every fee in the game derives from
                // this one, the shipped default is 1,000, and a page that had the price hardcoded would
                // agree with a server running the default on every screen. Against 1,337 it cannot: the
                // number the browser prints is the number this server was configured with, or the test
                // fails. Same reason the seeded offer below asks 24,680 rather than 25,000.
                .UseSetting("Game:BreedingFeeSats", BreedingFeeSats.ToString()));

            // The TestServer-backed host the base class requires. Built before the builder is retargeted.
            var testHost = builder.Build();

            builder.ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0"));
            _kestrel = builder.Build();
            _kestrel.Start();

            testHost.Start();
            return testHost;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _kestrel?.Dispose();
            base.Dispose(disposing);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PlayableAppCollection : ICollectionFixture<PlayableAppFixture>
{
    public const string Name = "playable-app";
}
