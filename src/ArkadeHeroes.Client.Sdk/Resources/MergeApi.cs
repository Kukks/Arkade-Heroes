using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Two-phase merge/fusion (commit → deposit → reveal) + the covenant escrow params for a trustless reclaim.</summary>
public sealed class MergeApi(ArkadeHeroesClient client)
{
    public Task<MergeCommitResponse> CommitAsync(MergeCommitRequest req) => client.PostAsync<MergeCommitResponse>("/api/merge/commit", req);
    public Task<MergeRevealResponse> RevealAsync(string mergeId, MergeRevealRequest req) => client.PostAsync<MergeRevealResponse>($"/api/merge/{mergeId}/reveal", req);
    public Task<MergeEscrowParams> EscrowAsync(string mergeId) => client.GetAsync<MergeEscrowParams>($"/api/merges/{mergeId}/escrow");
}
