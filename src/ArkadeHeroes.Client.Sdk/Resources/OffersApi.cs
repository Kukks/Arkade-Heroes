using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Marketplace resting offers (item + whole-hero): list, create, inspect, reclaim params, and hero-purchase claim.</summary>
public sealed class OffersApi(ArkadeHeroesClient client)
{
    public Task<List<OfferDto>> ListAsync() => client.GetAsync<List<OfferDto>>("/api/offers");
    public Task<CreateOfferResponse> CreateItemAsync(CreateOfferRequest req) => client.PostAsync<CreateOfferResponse>("/api/offers", req);
    public Task<CreateOfferResponse> CreateHeroAsync(CreateHeroOfferRequest req) => client.PostAsync<CreateOfferResponse>("/api/offers/hero", req);
    public Task<OfferDto> GetAsync(string offerId) => client.GetAsync<OfferDto>($"/api/offers/{offerId}");
    public Task<OfferParams> ParamsAsync(string offerId) => client.GetAsync<OfferParams>($"/api/offers/{offerId}/params");
    public Task<TransferResponse> ClaimHeroAsync(string offerId) => client.PostAsync<TransferResponse>($"/api/offers/{offerId}/claim-hero");
}
