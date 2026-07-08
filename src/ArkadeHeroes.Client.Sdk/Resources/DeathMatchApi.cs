using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Winner-takes-all death-match (open → accept → settle) + the joint escrow params for a trustless post-expiry reclaim.</summary>
public sealed class DeathMatchApi(ArkadeHeroesClient client)
{
    public Task<DeathMatchOpenResponse> OpenAsync(DeathMatchOpenRequest req) => client.PostAsync<DeathMatchOpenResponse>("/api/deathmatch/open", req);
    public Task<DeathMatchAcceptResponse> AcceptAsync(string deathMatchId) => client.PostAsync<DeathMatchAcceptResponse>($"/api/deathmatch/{deathMatchId}/accept");
    public Task<DeathMatchSettleResponse> SettleAsync(string deathMatchId, DeathMatchSettleRequest req) => client.PostAsync<DeathMatchSettleResponse>($"/api/deathmatch/{deathMatchId}/settle", req);
    public Task<DeathMatchJointEscrowParams> EscrowAsync(string deathMatchId) => client.GetAsync<DeathMatchJointEscrowParams>($"/api/deathmatch/{deathMatchId}/escrow");
}
