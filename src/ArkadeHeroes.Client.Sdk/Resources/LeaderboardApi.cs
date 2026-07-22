using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Read-only boards: the win/level leaderboard and the genome-rarity ranking.</summary>
public sealed class LeaderboardApi(ArkadeHeroesClient client)
{
    public Task<List<LeaderboardEntryDto>> TopAsync() => client.GetAsync<List<LeaderboardEntryDto>>("/api/leaderboard");
    /// <summary>The current season's ranked ladder (staked-match wins this season) + when it resets.</summary>
    public Task<SeasonLeaderboardDto> SeasonAsync() => client.GetAsync<SeasonLeaderboardDto>("/api/leaderboard/season");
    public Task<List<HeroDto>> RarestAsync() => client.GetAsync<List<HeroDto>>("/api/rarest");
    /// <summary>Heroes whose genome hits a named Fancy set (a concrete breeding target beyond rarity score).</summary>
    public Task<List<HeroDto>> FanciesAsync() => client.GetAsync<List<HeroDto>>("/api/fancies");
    /// <summary>Generation-0 heroes — the original mints, the scarcest lineage (gen-0 supply never grows).</summary>
    public Task<List<HeroDto>> FoundersAsync() => client.GetAsync<List<HeroDto>>("/api/founders");

    /// <summary>The spectator feed — resolved fights worth watching, with the reason each one made the cut.</summary>
    public Task<List<HighlightDto>> HighlightsAsync() => client.GetAsync<List<HighlightDto>>("/api/highlights");

    /// <summary>The Fancy discovery race — who first found each named set, and which are still unclaimed.</summary>
    public Task<List<FancyDiscoveryDto>> FancyDiscoveriesAsync() =>
        client.GetAsync<List<FancyDiscoveryDto>>("/api/fancies/discoveries");
}
