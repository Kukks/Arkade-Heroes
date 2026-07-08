using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Server-cached progression receipts for a hero (signed public facts; players hold their own copies too).</summary>
public sealed class ReceiptsApi(ArkadeHeroesClient client)
{
    public Task<List<ProgressionReceiptDto>> ForHeroAsync(string heroId) => client.GetAsync<List<ProgressionReceiptDto>>($"/api/receipts/hero/{heroId}");
}
