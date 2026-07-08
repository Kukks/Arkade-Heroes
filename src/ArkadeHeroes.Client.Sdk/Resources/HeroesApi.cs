using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Hero roster, starters, and per-hero actions (equip/unequip/transfer).</summary>
public sealed class HeroesApi(ArkadeHeroesClient client)
{
    public Task<StarterResponse> ClaimStartersAsync() => client.PostAsync<StarterResponse>("/api/heroes/starter");
    public Task<List<HeroDto>> MineAsync() => client.GetAsync<List<HeroDto>>("/api/heroes/mine");
    public Task<List<HeroDto>> AllAsync() => client.GetAsync<List<HeroDto>>("/api/heroes");
    public Task<HeroDto> GetAsync(string heroId) => client.GetAsync<HeroDto>($"/api/heroes/{heroId}");
    public Task<EquipResponse> EquipAsync(string heroId, EquipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/equip", req);
    public Task<EquipResponse> UnequipAsync(string heroId, UnequipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/unequip", req);
    public Task<TransferResponse> TransferAsync(string heroId, TransferRequest req) => client.PostAsync<TransferResponse>($"/api/heroes/{heroId}/transfer", req);
}
