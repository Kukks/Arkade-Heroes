using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Team 3v3 squad matches: open a two-lineup wagered match, accept, resolve the best-of-3, and replay.</summary>
public sealed class SquadApi(ArkadeHeroesClient client)
{
    public Task<SquadOpenResponse> OpenAsync(OpenSquadMatchRequest req) =>
        client.PostAsync<SquadOpenResponse>("/api/squad/open", req);
    public Task<SquadAcceptResponse> AcceptAsync(string matchId) =>
        client.PostAsync<SquadAcceptResponse>($"/api/squad/{matchId}/accept");
    public Task<SquadResolveResponse> ResolveAsync(string matchId, FightRequest req) =>
        client.PostAsync<SquadResolveResponse>($"/api/squad/{matchId}/resolve", req);
    public Task<SquadReplayDto> ReplayAsync(string matchId) =>
        client.GetAsync<SquadReplayDto>($"/api/squad/{matchId}/replay");
    public Task<List<SquadMatchDto>> ListAsync(string? status = null) =>
        client.GetAsync<List<SquadMatchDto>>($"/api/squad{(status is null ? "" : $"?status={status}")}");
}
