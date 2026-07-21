using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Buy-in tournaments: open a bracket, join it, and resolve it once full — prizes go to the podium.</summary>
public sealed class TournamentApi(ArkadeHeroesClient client)
{
    public Task<TournamentEntryResponse> OpenAsync(OpenTournamentRequest req) => client.PostAsync<TournamentEntryResponse>("/api/tournament/open", req);
    public Task<TournamentEntryResponse> JoinAsync(string id, JoinTournamentRequest req) => client.PostAsync<TournamentEntryResponse>($"/api/tournament/{id}/join", req);
    /// <summary>Resolve a full bracket, revealing a nonce; pays the podium out of the pot minus the house rake.</summary>
    public Task<TournamentResolveResponse> ResolveAsync(string id, FightRequest req) => client.PostAsync<TournamentResolveResponse>($"/api/tournament/{id}/resolve", req);
    public Task<List<TournamentDto>> ListAsync() => client.GetAsync<List<TournamentDto>>("/api/tournament");
    public Task<TournamentDto> GetAsync(string id) => client.GetAsync<TournamentDto>($"/api/tournament/{id}");
}
