using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Winner-takes-all death-match (open → accept → settle) + the joint escrow params for a trustless post-expiry reclaim.</summary>
public sealed class DeathMatchApi(ArkadeHeroesClient client)
{
    public Task<List<DeathMatchDto>> ListAsync() => client.GetAsync<List<DeathMatchDto>>("/api/deathmatch");
    /// <summary>Public spectator replay of a SETTLED death-match — snapshots + fight + the seed to verify it.</summary>
    public Task<MatchReplayDto> ReplayAsync(string deathMatchId) => client.GetAsync<MatchReplayDto>($"/api/deathmatch/{deathMatchId}/replay");
    public Task<DeathMatchOpenResponse> OpenAsync(DeathMatchOpenRequest req) => client.PostAsync<DeathMatchOpenResponse>("/api/deathmatch/open", req);
    public Task<DeathMatchAcceptResponse> AcceptAsync(string deathMatchId) => client.PostAsync<DeathMatchAcceptResponse>($"/api/deathmatch/{deathMatchId}/accept");
    /// <summary>Whether <see cref="SettleAsync"/> would get past its funding gates yet — poll this rather
    /// than learning "not yet" from a 400, which a browser console reports as a failure on a healthy run.</summary>
    public Task<DeathMatchReadinessDto> ReadinessAsync(string deathMatchId) => client.GetAsync<DeathMatchReadinessDto>($"/api/deathmatch/{deathMatchId}/readiness");
    public Task<DeathMatchSettleResponse> SettleAsync(string deathMatchId, DeathMatchSettleRequest req) => client.PostAsync<DeathMatchSettleResponse>($"/api/deathmatch/{deathMatchId}/settle", req);
    public Task<DeathMatchJointEscrowParams> EscrowAsync(string deathMatchId) => client.GetAsync<DeathMatchJointEscrowParams>($"/api/deathmatch/{deathMatchId}/escrow");
}
