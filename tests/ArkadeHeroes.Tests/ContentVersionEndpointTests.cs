using System.Text.Json;
using System.Text.Json.Nodes;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// GET /api/content/{version} over the real HTTP surface, and the stamp that makes it reachable.
///
/// This is the twin of <see cref="ConfigVersionEndpointTests"/> and it exists for the same reason. Item
/// stats are combat inputs — <c>FairnessAudit.RebuildHero</c> turns an equipped item id back into stats
/// before replaying — so a verifier holding different content than the match ran on replays a different
/// fight and prints "SERVER CHEATED" over an honest result. A known version therefore resolves to content
/// that HASHES BACK to it (check, don't trust); an unknown version is a 404, never a quiet fall back to
/// the client's own compiled-in pack.
/// </summary>
public class ContentVersionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContentVersionEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ServesTheDefaultVersion_AndItRoundTripsToItsOwnId()
    {
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        var served = await api.Content.GetAsync(ContentPackVersion.Default);

        Assert.Equal(ContentPackVersion.Default, served.Version);

        // The round trip that makes the fetch trustless: re-parse the served bytes with the same loader
        // and recompute. Because the wire form IS the authored form, this cannot drift the way a flattened
        // mirror of the schema could.
        var rebuilt = ContentPackLoader.Parse(served.ItemsJson, served.DungeonsJson);
        Assert.Equal(ContentPackVersion.Default, ContentPackVersion.Compute(rebuilt));

        // …and it is the same content, not merely the same hash.
        Assert.Equal(ContentPack.Default.Items.Count, rebuilt.Items.Count);
        foreach (var item in ContentPack.Default.Items)
            Assert.Equal(ContentValidation.Seal(item), ContentValidation.Seal(rebuilt.FindItem(item.Id)!));
    }

    [Fact]
    public async Task AnUnknownVersionIs404_AndTheResolverRefusesRatherThanFallingBack()
    {
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        const string unknown = "00000000000000000000000000000000000000000000000000000000feedface";

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => api.Content.GetAsync(unknown));

        // The resolver every verifier goes through refuses — it does NOT hand back the local pack. A silent
        // substitution here is precisely the bug this mechanism exists to remove.
        var (pack, error) = await api.Content.ResolveAsync(unknown);
        Assert.Null(pack);
        Assert.NotNull(error);
        Assert.Contains("cannot verify", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAbsentStampResolvesToTheLocalPack_SoHistoricalReplaysKeepVerifying()
    {
        // "" is a FACT about artifacts resolved before content stamping existed — they ran on the gear
        // their binary compiled in — not a fallback for an unknown id.
        var api = new ArkadeHeroesClient(_factory.CreateClient());

        var (pack, error) = await api.Content.ResolveAsync("");
        Assert.Null(error);
        Assert.Same(ContentPack.Default, pack);

        var (nullPack, nullError) = await api.Content.ResolveAsync(null);
        Assert.Null(nullError);
        Assert.Same(ContentPack.Default, nullPack);
    }

    [Fact]
    public async Task TheDefaultStampResolvesOfflineWithoutARoundTrip()
    {
        // A client whose own pack already IS the stamped one must not need the network to verify.
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        var (pack, error) = await api.Content.ResolveAsync(ContentPackVersion.Default);
        Assert.Null(error);
        Assert.Same(ContentPack.Default, pack);
    }

    [Fact]
    public async Task AResolvedMatchCarriesTheContentStamp_OnBothTheFightAndTheSpectatorReplay()
    {
        var (alice, _) = await _factory.RegisterAsync("ContentStampAlice");
        var (bob, _) = await _factory.RegisterAsync("ContentStampBob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("content-stamp-nonce"));
        Assert.Equal(ContentPackVersion.Default, fight.ContentVersion);

        // The public spectator replay reads the stamp RECORDED on the session at resolve time.
        var replay = await alice.Matches.ReplayAsync(open.MatchId);
        Assert.Equal(ContentPackVersion.Default, replay.ContentVersion);

        // And the round trip closes: the stamp resolves, and the match verifies under the content it names.
        var (pack, error) = await alice.Content.ResolveAsync(replay.ContentVersion);
        Assert.Null(error);
        Assert.NotNull(pack);

        var (config, configError) = await alice.Config.ResolveAsync(replay.ConfigVersion);
        Assert.Null(configError);
        var (ok, detail) = FairnessAudit.VerifyMatch(
            open.MatchId, "content-stamp-nonce", open.CommitmentHex, fight, config);
        Assert.True(ok, detail);
    }

    [Fact]
    public async Task GauntletAndTrialsRunsCarryTheContentStamp()
    {
        // The gauntlet is the run whose LADDER and DROP are authored content, so it is the one where an
        // unstamped outcome would be least verifiable.
        var (alice, _) = await _factory.RegisterAsync("ContentStampRunner");
        var heroes = await alice.ClaimStartersAsync();

        var gOpen = await alice.Gauntlet.OpenAsync(heroes[0].Id);
        await alice.PayInvoiceAsync(gOpen.FeeInvoice.InvoiceId);
        var gRun = await alice.Gauntlet.RunAsync(gOpen.GauntletId, "content-g-nonce");
        Assert.Equal(ContentPackVersion.Default, gRun.ContentVersion);

        var tOpen = await alice.Trials.OpenAsync(heroes[1].Id);
        var tRun = await alice.Trials.RunAsync(tOpen.TrialsId, "content-t-nonce");
        Assert.Equal(ContentPackVersion.Default, tRun.ContentVersion);
    }

    /// <summary>
    /// Wire compatibility, both directions. The stamp is a TRAILING OPTIONAL field, so an older client that
    /// has never seen it must still deserialize today's payload, and today's client must still read an
    /// older server's payload that omits it — landing on "", which it correctly treats as "the content this
    /// binary compiled in". Getting this wrong would break every already-deployed client at once.
    /// </summary>
    [Fact]
    public void TheContentStampIsWireCompatibleInBothDirections()
    {
        // Uses the SDK's own serializer settings, so this exercises the real wire contract.
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var hero = new HeroDto(
            "h1", "h1", "owner", new string('a', 64), 0, "Fire", 1, 0, 100,
            new StatsDto(50, 10, 10, 10, 10, 10, 5, 5), [], new Dictionary<string, string>(),
            0, null, null, null, null, null, null);
        var result = new BattleResultDto("w", "l", 3, [], 10, 20);

        // Today's payload → JSON → back: the stamp actually arrives, or the mechanism is inert on the wire.
        var replay = new MatchReplayDto(hero, hero, result, "w", "cc", "dd", "ee", "n",
            ArkadeHeroes.Core.GameConfigVersion.Default, ContentPackVersion.Default);
        var json = JsonSerializer.Serialize(replay, wire);
        var back = JsonSerializer.Deserialize<MatchReplayDto>(json, wire)!;
        Assert.Equal(ContentPackVersion.Default, back.ContentVersion);

        // An OLDER server's payload: the property simply is not there. It must land on "" — which the
        // resolver reads as "unstamped" — rather than throwing and faulting every installed client.
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("contentVersion"), "the DTO did not serialize a contentVersion property");
        var legacy = JsonSerializer.Deserialize<MatchReplayDto>(node.ToJsonString(), wire)!;
        Assert.Equal("", legacy.ContentVersion);
        Assert.Equal(ArkadeHeroes.Core.GameConfigVersion.Default, legacy.ConfigVersion);
    }
}
