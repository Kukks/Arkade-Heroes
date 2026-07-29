using System.Text.Json;
using System.Text.Json.Nodes;
using ArkadeHeroes.Core;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The backward-compatibility bar, checked at the JSON layer rather than assumed from the C# signature.
///
/// Every ConfigVersion stamp is a TRAILING OPTIONAL parameter, which is what keeps installed clients
/// working — but "trailing optional" only actually protects them if System.Text.Json tolerates the property
/// being ABSENT from the payload (an older server that has never heard of stamps) and yields a value the
/// resolver reads as "unstamped". If deserialization instead threw, or produced something the resolver sent
/// to the network, a routine deploy would fault every deployed client on the fairness path.
///
/// These use the SDK's own serializer settings (HttpClientJsonExtensions defaults to
/// <see cref="JsonSerializerDefaults.Web"/>), so they exercise the real wire contract.
/// </summary>
public class ConfigStampWireCompatTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static HeroDto Hero(string id) => new(
        id, id, "owner", new string('a', 64), 0, "Fire", 1, 0, 100,
        new StatsDto(50, 10, 10, 10, 10, 10, 5, 5), [], new Dictionary<string, string>(),
        0, null, null, null, null, null, null);

    private static BattleResultDto Result() => new("w", "l", 3, [], 10, 20);

    /// <summary>Serializes <paramref name="dto"/>, DROPS the stamp property (what an older server's
    /// payload looks like), and reads it back — the exact path an installed client takes.</summary>
    private static T RoundTripWithoutStamp<T>(T dto)
    {
        var node = JsonSerializer.SerializeToNode(dto, Wire)!.AsObject();
        Assert.True(node.Remove("configVersion"), "the DTO did not serialize a configVersion property");
        return JsonSerializer.Deserialize<T>(node.ToJsonString(), Wire)!;
    }

    [Fact]
    public void EveryStampedDto_DeserializesFromAPayloadThatOmitsTheStamp()
    {
        var hero = Hero("h1");
        var result = Result();

        var fight = RoundTripWithoutStamp(new FightResponse(
            result, "aa", "bb", 1, 2, hero, hero, hero, hero, 5, 10, null, "some-version"));
        Assert.Equal("bb", fight.EntropyHex);
        Assert.Equal(5, fight.WagerSats);

        var replay = RoundTripWithoutStamp(new MatchReplayDto(
            hero, hero, result, "w", "cc", "dd", "ee", "n", "some-version"));
        Assert.Equal("n", replay.Nonce);

        var settle = RoundTripWithoutStamp(new DeathMatchSettleResponse(
            result, "w", "l", hero, hero, "aa", "bb", null, true, 1, "ff", null, "some-version"));
        Assert.True(settle.Minted);

        var squad = RoundTripWithoutStamp(new SquadReplayDto(
            [hero], [hero], new SquadResultDto(true, 2, 1, []), "cc", "dd", "ee", "n", "some-version"));
        Assert.True(squad.Result.ChallengerWon);

        var tournament = RoundTripWithoutStamp(new TournamentReplayDto(
            [hero], [], "champ", "cc", "dd", "ee", "n", "entrants", "some-version"));
        Assert.Equal("entrants", tournament.EntrantsCommitmentHex);

        var receipt = new ProgressionReceiptDto("gauntlet", "g", "h1", "", "h1", "aa", "n", "cc",
            0, 0, 1, 1, 0, "", "");
        var gauntlet = RoundTripWithoutStamp(new GauntletRunResponse(
            5, [], 10, 2, null, null, hero, "aa", "bb", receipt, "some-version"));
        Assert.Equal(5, gauntlet.WavesCleared);

        var trials = RoundTripWithoutStamp(new TrialsRunResponse(
            7, [], "Title", 7, "None", hero, "aa", "bb", receipt, "some-version"));
        Assert.Equal(7, trials.WavesCleared);

        // The stamp field on each is now whatever STJ produced for a missing optional parameter — either the
        // declared "" or null. Both are the SAME instruction to the resolver: "unstamped, so GameConfig.Default".
        foreach (var stamp in new[]
                 {
                     fight.ConfigVersion, replay.ConfigVersion, settle.ConfigVersion,
                     squad.ConfigVersion, tournament.ConfigVersion,
                     gauntlet.ConfigVersion, trials.ConfigVersion,
                 })
            Assert.True(string.IsNullOrEmpty(stamp),
                $"an omitted stamp must read as unstamped, got '{stamp}'");
    }

    [Fact]
    public void AStampedPayload_StillDeserializesTheStamp()
    {
        // The other direction: when the property IS present it must actually arrive, or the whole
        // mechanism is inert on the wire.
        var hero = Hero("h1");
        var json = JsonSerializer.Serialize(
            new MatchReplayDto(hero, hero, Result(), "w", "cc", "dd", "ee", "n", GameConfigVersion.Default),
            Wire);
        var back = JsonSerializer.Deserialize<MatchReplayDto>(json, Wire)!;
        Assert.Equal(GameConfigVersion.Default, back.ConfigVersion);
    }

    [Fact]
    public void AnOlderClient_IgnoresTheNewStampProperty()
    {
        // The mirror case: a deployed client whose DTO has no ConfigVersion member reading a NEW server's
        // payload. STJ ignores unknown properties by default, so the extra field is inert rather than fatal.
        // MatchDto stands in for "a record without the stamp" — same serializer, same defaults.
        var node = JsonSerializer.SerializeToNode(
            new MatchDto("m1", "c", "d", "resolved", "cc", null, 5, "p"), Wire)!.AsObject();
        node["configVersion"] = GameConfigVersion.Default;   // the field the older client has never seen

        var back = JsonSerializer.Deserialize<MatchDto>(node.ToJsonString(), Wire)!;
        Assert.Equal("m1", back.MatchId);
        Assert.Equal(5, back.WagerSats);
    }
}
