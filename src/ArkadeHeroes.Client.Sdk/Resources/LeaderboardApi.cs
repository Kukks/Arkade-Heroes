using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Read-only boards: the win/level leaderboard and the genome-rarity ranking.</summary>
public sealed class LeaderboardApi(ArkadeHeroesClient client)
{
    public Task<List<LeaderboardEntryDto>> TopAsync() => client.GetAsync<List<LeaderboardEntryDto>>("/api/leaderboard");
    public Task<List<HeroDto>> RarestAsync() => client.GetAsync<List<HeroDto>>("/api/rarest");
}
