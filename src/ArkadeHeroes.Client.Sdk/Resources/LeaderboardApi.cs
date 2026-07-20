using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Read-only boards: the win/level leaderboard and the genome-rarity ranking.</summary>
public sealed class LeaderboardApi(ArkadeHeroesClient client)
{
    public Task<List<LeaderboardEntryDto>> TopAsync() => client.GetAsync<List<LeaderboardEntryDto>>("/api/leaderboard");
    /// <summary>The current season's ranked ladder (staked-match wins this season) + when it resets.</summary>
    public Task<SeasonLeaderboardDto> SeasonAsync() => client.GetAsync<SeasonLeaderboardDto>("/api/leaderboard/season");
    public Task<List<HeroDto>> RarestAsync() => client.GetAsync<List<HeroDto>>("/api/rarest");
}
