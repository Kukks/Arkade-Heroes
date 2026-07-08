using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Equipment shop: catalog, buy (fee invoice), and claim (delivers a fungible item asset unit).</summary>
public sealed class ItemsApi(ArkadeHeroesClient client)
{
    public Task<List<ItemDto>> ShopAsync() => client.GetAsync<List<ItemDto>>("/api/items");
    public Task<ItemInvoiceResponse> BuyAsync(string itemId) => client.PostAsync<ItemInvoiceResponse>($"/api/items/{itemId}/buy");
    public Task<ClaimItemResponse> ClaimAsync(ClaimItemRequest req) => client.PostAsync<ClaimItemResponse>("/api/items/claim", req);
}
