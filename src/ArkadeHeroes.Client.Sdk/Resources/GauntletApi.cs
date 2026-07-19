using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Solo PvE gauntlet (F1): open (commit + fee invoice) → pay the fee → run the 5-wave ghost ladder.</summary>
public sealed class GauntletApi(ArkadeHeroesClient client)
{
    public Task<GauntletOpenResponse> OpenAsync(string heroId) =>
        client.PostAsync<GauntletOpenResponse>("/api/gauntlet/open", new GauntletOpenRequest(heroId));

    public Task<GauntletRunResponse> RunAsync(string gauntletId, string nonce) =>
        client.PostAsync<GauntletRunResponse>($"/api/gauntlet/{gauntletId}/run", new GauntletRunRequest(nonce));
}
