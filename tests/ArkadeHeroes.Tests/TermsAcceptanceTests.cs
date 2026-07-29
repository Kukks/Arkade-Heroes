using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The Terms-of-Use acceptance. This game stakes REAL BITCOIN and destroys assets permanently — a
/// death-match burns the loser, a fusion burns both inputs, a lost recovery phrase is gone — and the terms
/// are the only place a player is told so. An acceptance that can't be produced later is worth very little,
/// which is why these tests care about three things: it SURVIVES (a restart, a cleared browser), it EXPIRES
/// (a changed document re-asks), and it can't be poisoned by a garbage version.
/// </summary>
public class TermsAcceptanceTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    /// <summary>A raw client on this player's bearer token, for posting bodies the typed SDK won't build.</summary>
    private static HttpClient RawClientFor(WebApplicationFactory<Program> factory, PlayerDto player)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        return http;
    }

    // ── It survives ────────────────────────────────────────────────────────

    [Fact]
    public async Task Acceptance_SurvivesARestart()
    {
        // The whole point of recording it server-side. Browser-local storage is one cache clear from gone
        // and lives on the player's own machine; only the row can still be produced after a bounce.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-terms-{Guid.NewGuid():N}.db");
        try
        {
            string playerId;
            DateTimeOffset acceptedAt;
            using (var first = HostOn(dbPath))
            {
                var (alice, player) = await first.RegisterAsync("Durable-Accepter");
                playerId = player.PlayerId;

                var recorded = await alice.Players.AcceptTermsAsync(Terms.CurrentVersion);
                Assert.Equal(Terms.CurrentVersion, recorded.AcceptedVersion);
                Assert.NotNull(recorded.AcceptedAtUtc);
                acceptedAt = recorded.AcceptedAtUtc!.Value;
            }

            // ── restart: a brand-new host and GameStore over the same database file ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Players.ContainsKey(playerId));
            var recovered = store.Players[playerId];
            Assert.Equal(Terms.CurrentVersion, recovered.TermsAcceptedVersion);
            Assert.NotNull(recovered.TermsAcceptedAtUtc);
            // The timestamp is evidence, not decoration — it has to come back as the same instant.
            Assert.Equal(acceptedAt.ToUnixTimeSeconds(), recovered.TermsAcceptedAtUtc!.Value.ToUnixTimeSeconds());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AcceptanceGivenAtRegistration_SurvivesARestart()
    {
        // The browser accepts BEFORE a player exists (nothing irreversible may happen first), so the version
        // rides along on the registration call. That path has to be just as durable as the explicit POST.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-terms-{Guid.NewGuid():N}.db");
        try
        {
            string playerId;
            using (var first = HostOn(dbPath))
            {
                var client = new ArkadeHeroesClient(first.CreateClient());
                var player = await client.Players.RegisterAsync(new RegisterPlayerRequest(
                    "Accepts-At-Signup", $"sim-wallet-{Guid.NewGuid():N}",
                    AcceptedTermsVersion: Terms.CurrentVersion));
                playerId = player.PlayerId;
                Assert.Equal(Terms.CurrentVersion, player.TermsAcceptedVersion);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var recovered = restarted.Services.GetRequiredService<GameStore>().Players[playerId];
            Assert.Equal(Terms.CurrentVersion, recovered.TermsAcceptedVersion);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    // ── It expires when the document changes ───────────────────────────────

    [Fact]
    public async Task AcceptedVersionN_IsRepromptedWhenTheCurrentVersionBecomesNPlusOne()
    {
        // The re-prompt rule, driven by a REAL recorded acceptance rather than a hand-made number: read back
        // what the server stored, then ask the same predicate the browser gate asks — first against today's
        // version, then against the version docs/TERMS.md would carry after a material edit.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Reprompted-Player");
        await alice.Players.AcceptTermsAsync(Terms.CurrentVersion);

        var onFile = (await alice.Players.TermsAsync()).AcceptedVersion;
        var n = Assert.IsType<int>(onFile);

        Assert.True(Terms.Satisfies(n, n), "an acceptance of the current version must not re-prompt");
        Assert.False(Terms.Satisfies(n, n + 1),
            "bumping Terms.CurrentVersion is how a changed document gets re-agreed — v(N) must not satisfy v(N+1)");

        // And the /players/me projection the browser actually reads carries the same stale version, so the
        // gate can see it without a second round trip.
        Assert.Equal(n, (await alice.Players.MeAsync()).TermsAcceptedVersion);
    }

    [Fact]
    public void NoAcceptanceOnFile_AlwaysPrompts()
    {
        Assert.False(Terms.Satisfies(null), "a player who never accepted must always be asked");
        Assert.False(Terms.Satisfies(null, 1));
    }

    [Fact]
    public async Task ANewerAcceptanceIsNeverWalkedBackByAStaleOne()
    {
        // A tab left open on an older build can post an out-of-date version after the player already accepted
        // a newer one somewhere else. Recording it would re-prompt them for terms they've already agreed to.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, player) = await factory.RegisterAsync("Stale-Tab");
        var store = factory.Services.GetRequiredService<GameStore>();

        await alice.Players.AcceptTermsAsync(Terms.CurrentVersion);
        // Stand in for "a newer version exists and was accepted" without editing the constant.
        store.Players[player.PlayerId].TermsAcceptedVersion = Terms.CurrentVersion + 5;

        await alice.Players.AcceptTermsAsync(Terms.CurrentVersion);   // the stale tab, arriving late

        Assert.Equal(Terms.CurrentVersion + 5, store.Players[player.PlayerId].TermsAcceptedVersion);
    }

    // ── It refuses garbage rather than recording it ────────────────────────

    [Theory]
    [InlineData(0)]                 // what a missing "version" field deserialises into
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task AVersionThatIsNotARealVersion_IsRefusedAndNothingIsRecorded(int version)
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, player) = await factory.RegisterAsync($"Garbage-{version}");

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Players.AcceptTermsAsync(version));

        Assert.Null((await alice.Players.TermsAsync()).AcceptedVersion);
        Assert.Null(factory.Services.GetRequiredService<GameStore>().Players[player.PlayerId].TermsAcceptedVersion);
    }

    [Fact]
    public async Task AVersionFromTheFuture_IsRefused()
    {
        // The dangerous one: a stored "accepted v9999" would silently satisfy every future bump, so the
        // player would never again be asked about terms they never read.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, player) = await factory.RegisterAsync("Future-Version");

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Players.AcceptTermsAsync(Terms.CurrentVersion + 1));

        Assert.Null(factory.Services.GetRequiredService<GameStore>().Players[player.PlayerId].TermsAcceptedVersion);
    }

    [Fact]
    public async Task AnAcceptanceWithNoVersionAtAll_IsRefusedAndNothingIsRecorded()
    {
        // A body with no version, and an empty body: both are "missing", and both must be refused loudly
        // rather than quietly recorded as version zero.
        using var factory = new WebApplicationFactory<Program>();
        var (_, player) = await factory.RegisterAsync("Missing-Version");
        using var raw = RawClientFor(factory, player);

        var noField = await raw.PostAsJsonAsync("/api/players/me/terms", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noField.StatusCode);

        var empty = await raw.PostAsync("/api/players/me/terms", null);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Null(factory.Services.GetRequiredService<GameStore>().Players[player.PlayerId].TermsAcceptedVersion);
    }

    [Fact]
    public async Task RegisteringWithAGarbageAcceptedVersion_CreatesNoPlayerAtAll()
    {
        // Registration carries the acceptance for a brand-new player. A nonsense version there must fail the
        // whole registration rather than mint a player whose acceptance record is meaningless.
        using var factory = new WebApplicationFactory<Program>();
        var client = new ArkadeHeroesClient(factory.CreateClient());

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => client.Players.RegisterAsync(
            new RegisterPlayerRequest("Bad-Signup", $"sim-wallet-{Guid.NewGuid():N}",
                AcceptedTermsVersion: Terms.CurrentVersion + 99)));

        Assert.DoesNotContain(factory.Services.GetRequiredService<GameStore>().Players.Values,
            p => p.Name == "Bad-Signup");
    }

    [Fact]
    public async Task RegisteringWithNoAcceptanceAtAll_StillWorksAndRecordsNothing()
    {
        // Every client that predates the terms screen — the console client, the rest of this suite — keeps
        // registering with no acceptance. Recording must not become a hard gate by accident.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("No-Terms-Offered");
        Assert.Null((await alice.Players.MeAsync()).TermsAcceptedVersion);
        Assert.Null((await alice.Players.TermsAsync()).AcceptedAtUtc);
    }

    // ── The opt-in server-side gate ────────────────────────────────────────

    [Fact]
    public async Task WithEnforcementOn_StartersAreRefusedUntilTheTermsAreAccepted()
    {
        // Default-off (every existing client predates any terms screen), but a deployment staking real
        // bitcoin turns it on so the browser gate isn't the only thing standing between a player and a mint.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Game:RequireTermsAcceptance", "true"));
        var (alice, _) = await factory.RegisterAsync("Gated-Player");

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.ClaimStartersAsync());
        Assert.Contains("Terms of Use", refused.Message);

        await alice.Players.AcceptTermsAsync(Terms.CurrentVersion);
        Assert.Equal(2, (await alice.ClaimStartersAsync()).Count);
    }

    [Fact]
    public async Task WithEnforcementOff_TheDefault_StartersAreNotBlocked()
    {
        // The guard on the 522 tests that came before this feature: recording acceptance must not become a
        // hard gate by accident.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Ungated-Player");
        Assert.Equal(2, (await alice.ClaimStartersAsync()).Count);
        Assert.False((await alice.Players.TermsAsync()).AcceptanceRequired);
    }

    [Fact]
    public void AVersionIsOnlyAcceptableIfItActuallyExists()
    {
        Assert.False(Terms.IsAcceptableVersion(0));
        Assert.False(Terms.IsAcceptableVersion(-1));
        Assert.False(Terms.IsAcceptableVersion(Terms.CurrentVersion + 1));
        Assert.True(Terms.IsAcceptableVersion(Terms.CurrentVersion));
        Assert.True(Terms.IsAcceptableVersion(1));
    }
}
