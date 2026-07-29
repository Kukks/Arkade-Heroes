using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// GET /api/config/{version} over the real HTTP surface — the mechanism Dtos.cs documented and the wire
/// never had. A known version resolves to rules that HASH BACK to it (so the client can check rather than
/// trust); an unknown version is a 404, never a quiet fall back to GameConfig.Default.
/// </summary>
public class ConfigVersionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConfigVersionEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>The version id a server running <paramref name="options"/> stamps with — derived from the
    /// server's OWN projection rather than a hand-written GameConfig, so these tests keep asserting "the
    /// stamp is exactly this server's rules" without silently encoding a second copy of ToGameConfig().</summary>
    private static string StampFor(GameOptions options) => GameConfigVersion.Compute(options.ToGameConfig());

    [Fact]
    public async Task ServesTheDefaultVersion_AndItRoundTripsToItsOwnId()
    {
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        var rules = await api.Config.GetAsync(GameConfigVersion.Default);

        Assert.Equal(GameConfigVersion.Default, rules.Version);
        var rebuilt = rules.ToGameConfig();
        Assert.NotNull(rebuilt);
        Assert.Equal(GameConfigVersion.Default, GameConfigVersion.Compute(rebuilt!));
    }

    [Fact]
    public async Task AnUnknownVersionIs404_AndTheResolverRefusesRatherThanFallingBack()
    {
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        const string unknown = "00000000000000000000000000000000000000000000000000000000deadbeef";

        // The transport says 404 out loud.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => api.Config.GetAsync(unknown));

        // And the resolver every verifier goes through refuses — it does NOT hand back GameConfig.Default.
        // A silent Default here would be today's bug wearing a new hat: an honest replay resolved under
        // other rules would print "SERVER CHEATED".
        var (config, error) = await api.Config.ResolveAsync(unknown);
        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("cannot verify", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAbsentStampResolvesToDefault_SoHistoricalReplaysKeepVerifying()
    {
        // "" is a FACT about pre-stamp artifacts (they ran on Default), not a fallback for an unknown id.
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        var (config, error) = await api.Config.ResolveAsync("");
        Assert.Null(error);
        Assert.Same(GameConfig.Default, config);

        var (nullConfig, nullError) = await api.Config.ResolveAsync(null);
        Assert.Null(nullError);
        Assert.Same(GameConfig.Default, nullConfig);
    }

    [Fact]
    public async Task ARetunedServerPublishesAndServesItsOwnVersion()
    {
        // The reachable non-default path today: AbsorbChance is GameOptions-tunable AND verification-critical,
        // so a retuned server genuinely runs under rules whose id is not GameConfigVersion.Default.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "200"));
        var api = new ArkadeHeroesClient(factory.CreateClient());

        var info = await api.Chain.InfoAsync();
        var version = info.Config!.Version;
        Assert.NotEqual(GameConfigVersion.Default, version);
        Assert.Equal(StampFor(new GameOptions { AbsorbChance = 200 }), version);

        // Its own version resolves, and hashes back to what was asked for.
        var (resolved, error) = await api.Config.ResolveAsync(version);
        Assert.Null(error);
        Assert.NotNull(resolved);
        Assert.Equal(version, GameConfigVersion.Compute(resolved!));
        Assert.Equal(200, resolved!.Absorb.AbsorbChance);

        // A retuned server still serves GameConfig.Default, so every pre-retune replay it is still holding
        // can be verified against the rules it actually ran on.
        var (fallbackRules, fallbackError) = await api.Config.ResolveAsync(GameConfigVersion.Default);
        Assert.Null(fallbackError);
        Assert.Equal(GameConfigVersion.Default, GameConfigVersion.Compute(fallbackRules!));
    }

    [Fact]
    public async Task AResolvedMatchCarriesTheServersStamp_OnBothTheFightAndTheSpectatorReplay()
    {
        // The stamp must reach the wire on the artifact a verifier actually reads — otherwise everything
        // above is theory. Uses a retuned server so the expected stamp is provably not just "the default".
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "200"));
        var expected = StampFor(new GameOptions { AbsorbChance = 200 });

        var (alice, _) = await factory.RegisterAsync("StampAlice");
        var (bob, _) = await factory.RegisterAsync("StampBob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("stamp-fight-nonce"));
        Assert.Equal(expected, fight.ConfigVersion);

        // The public spectator replay reads the stamp RECORDED on the session at resolve time.
        var replay = await alice.Matches.ReplayAsync(open.MatchId);
        Assert.Equal(expected, replay.ConfigVersion);

        // And the round trip closes: the stamp resolves to rules that verify this very match.
        var (config, error) = await alice.Config.ResolveAsync(replay.ConfigVersion);
        Assert.Null(error);
        var (ok, detail) = FairnessAudit.VerifyMatch(
            open.MatchId, "stamp-fight-nonce", open.CommitmentHex, fight, config);
        Assert.True(ok, detail);
    }

    [Fact]
    public async Task GauntletAndTrialsRunsCarryTheStamp()
    {
        var (alice, _) = await _factory.RegisterAsync("StampRunner");
        var heroes = await alice.ClaimStartersAsync();

        var gOpen = await alice.Gauntlet.OpenAsync(heroes[0].Id);
        await alice.PayInvoiceAsync(gOpen.FeeInvoice.InvoiceId);
        var gRun = await alice.Gauntlet.RunAsync(gOpen.GauntletId, "stamp-g-nonce");
        Assert.Equal(StampFor(new GameOptions()), gRun.ConfigVersion);

        var tOpen = await alice.Trials.OpenAsync(heroes[1].Id);
        var tRun = await alice.Trials.RunAsync(tOpen.TrialsId, "stamp-t-nonce");
        Assert.Equal(StampFor(new GameOptions()), tRun.ConfigVersion);

        // An UNRETUNED server no longer stamps the compiled-in default: GameOptions turns innate abilities
        // on, so these runs name rules a client must resolve rather than assume. That is the point of the
        // stamp, and gauntlet/trials must carry it as much as a duel does.
        Assert.NotEqual(GameConfigVersion.Default, gRun.ConfigVersion);
        Assert.NotEqual(GameConfigVersion.Default, tRun.ConfigVersion);
    }
}
