using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Hero roster, starters, and per-hero actions (equip/unequip/transfer).</summary>
public sealed class HeroesApi(ArkadeHeroesClient client)
{
    public Task<StarterResponse> ClaimStartersAsync() => client.PostAsync<StarterResponse>("/api/heroes/starter");

    /// <summary>The signed-in player's heroes, rarity-ordered. Pass skip/take to page; omit for all.</summary>
    public Task<List<HeroDto>> MineAsync(int? skip = null, int? take = null)
        => client.GetAsync<List<HeroDto>>($"/api/heroes/mine{PageQuery(skip, take)}");

    /// <summary>All minted heroes, rarity-ordered. Pass skip/take to page; omit for all.</summary>
    public Task<List<HeroDto>> AllAsync(int? skip = null, int? take = null)
        => client.GetAsync<List<HeroDto>>($"/api/heroes{PageQuery(skip, take)}");

    private static string PageQuery(int? skip, int? take)
    {
        var parts = new List<string>(2);
        if (skip is int s) parts.Add($"skip={s}");
        if (take is int t) parts.Add($"take={t}");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
    public Task<HeroDto> GetAsync(string heroId) => client.GetAsync<HeroDto>($"/api/heroes/{heroId}");
    public Task<EquipResponse> EquipAsync(string heroId, EquipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/equip", req);
    public Task<EquipResponse> UnequipAsync(string heroId, UnequipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/unequip", req);
    public Task<TransferResponse> TransferAsync(string heroId, TransferRequest req) => client.PostAsync<TransferResponse>($"/api/heroes/{heroId}/transfer", req);
}
