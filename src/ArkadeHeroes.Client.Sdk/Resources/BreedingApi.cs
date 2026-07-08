using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Two-phase breeding (commit → deposit/pay → reveal) + the covenant escrow params for a trustless reclaim.</summary>
public sealed class BreedingApi(ArkadeHeroesClient client)
{
    public Task<BreedCommitResponse> CommitAsync(BreedCommitRequest req) => client.PostAsync<BreedCommitResponse>("/api/breeding/commit", req);
    public Task<BreedRevealResponse> RevealAsync(string breedingId, BreedRevealRequest req) => client.PostAsync<BreedRevealResponse>($"/api/breeding/{breedingId}/reveal", req);
    public Task<BreedEscrowParams> EscrowAsync(string breedingId) => client.GetAsync<BreedEscrowParams>($"/api/breedings/{breedingId}/escrow");
}
