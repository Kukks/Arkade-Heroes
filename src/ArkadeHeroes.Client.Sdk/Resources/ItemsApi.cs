using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Equipment shop: catalog, buy (fee invoice), and claim (delivers a fungible item asset unit).</summary>
public sealed class ItemsApi(ArkadeHeroesClient client)
{
    public Task<List<ItemDto>> ShopAsync() => client.GetAsync<List<ItemDto>>("/api/items");
    /// <summary>Catalog ids the signed-in player already owns (holds ≥1 unit of) — for shop "owned" markers.</summary>
    /// <summary>Item id → units held. The count matters: gear is allocated per hero, so owning one unit
    /// does not mean a second hero can be armed with it.</summary>
    public Task<Dictionary<string, long>> MineAsync() =>
        client.GetAsync<Dictionary<string, long>>("/api/items/mine");
    public Task<ItemInvoiceResponse> BuyAsync(string itemId) => client.PostAsync<ItemInvoiceResponse>($"/api/items/{itemId}/buy");
    public Task<ClaimItemResponse> ClaimAsync(ClaimItemRequest req) => client.PostAsync<ClaimItemResponse>("/api/items/claim", req);
}
