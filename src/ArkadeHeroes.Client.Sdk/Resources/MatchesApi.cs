using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Wagered + friendly matches (open → accept → fight), listing, matchmaking, and the per-party wager escrow params.</summary>
public sealed class MatchesApi(ArkadeHeroesClient client)
{
    public Task<OpenMatchResponse> OpenAsync(OpenMatchRequest req) => client.PostAsync<OpenMatchResponse>("/api/matches/open", req);
    public Task<AcceptMatchResponse> AcceptAsync(string matchId) => client.PostAsync<AcceptMatchResponse>($"/api/matches/{matchId}/accept");
    public Task<FightResponse> FightAsync(string matchId, FightRequest req) => client.PostAsync<FightResponse>($"/api/matches/{matchId}/fight", req);
    public Task<List<MatchDto>> ListAsync(string? status = null) =>
        client.GetAsync<List<MatchDto>>(status is null ? "/api/matches" : $"/api/matches?status={status}");
    public Task<MatchDto> GetAsync(string matchId) => client.GetAsync<MatchDto>($"/api/matches/{matchId}");
    public Task<WagerEscrowParams> EscrowAsync(string matchId) => client.GetAsync<WagerEscrowParams>($"/api/matches/{matchId}/escrow");
    public Task<List<OpponentSuggestionDto>> MatchmakingAsync(string heroId) => client.GetAsync<List<OpponentSuggestionDto>>($"/api/matchmaking/{heroId}");
    /// <summary>Public spectator replay of a RESOLVED match — snapshots + fight + the seed to verify it.</summary>
    public Task<MatchReplayDto> ReplayAsync(string matchId) => client.GetAsync<MatchReplayDto>($"/api/matches/{matchId}/replay");
}
