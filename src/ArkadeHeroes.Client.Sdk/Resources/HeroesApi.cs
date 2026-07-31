using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Hero roster, starters, and per-hero actions (equip/unequip/transfer).</summary>
public sealed class HeroesApi(ArkadeHeroesClient client)
{
    /// <summary>Bills the starter claim. Pay <see cref="StarterQuoteResponse.Fee"/> before claiming.</summary>
    public Task<StarterQuoteResponse> RequestStartersAsync() => client.PostAsync<StarterQuoteResponse>("/api/heroes/starter/quote");

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

    /// <summary>This hero's full provenance, newest first — birth, fights, trades, fusions, and its death.
    /// Served for a DESTROYED hero too, off its headstone; only an unknown id 404s.</summary>
    public Task<HeroTimelineDto> TimelineAsync(string heroId) => client.GetAsync<HeroTimelineDto>($"/api/heroes/{heroId}/timeline");

    /// <summary>A DESTROYED hero's headstone — what <see cref="GetAsync"/> cannot return, because a dead
    /// hero's row is erased. 404s (and so throws) for a hero that is alive or simply unknown.</summary>
    public Task<HeroTombstoneDto> TombstoneAsync(string heroId) => client.GetAsync<HeroTombstoneDto>($"/api/heroes/{heroId}/tombstone");

    public Task<EquipResponse> EquipAsync(string heroId, EquipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/equip", req);
    public Task<EquipResponse> UnequipAsync(string heroId, UnequipRequest req) => client.PostAsync<EquipResponse>($"/api/heroes/{heroId}/unequip", req);
    public Task<TransferResponse> TransferAsync(string heroId, TransferRequest req) => client.PostAsync<TransferResponse>($"/api/heroes/{heroId}/transfer", req);

    /// <summary>Request a custom, globally-unique name — returns the treasury fee-invoice to pay before confirming.</summary>
    public Task<RenameHeroResponse> RequestRenameAsync(string heroId, RenameHeroRequest req) => client.PostAsync<RenameHeroResponse>($"/api/heroes/{heroId}/rename", req);
    /// <summary>Apply a pending rename once its fee has cleared (or immediately when free).</summary>
    public Task<HeroDto> ConfirmRenameAsync(string heroId) => client.PostAsync<HeroDto>($"/api/heroes/{heroId}/rename/confirm");
}
