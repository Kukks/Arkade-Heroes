namespace ArkadeHeroes.Client.Sdk;

/// <summary>
/// InMemory-mode-only dev endpoints: they simulate the client-wallet chain actions
/// (pay/stake/fund/refund/reclaim/transfer) so a wallet-less caller can drive the
/// full lifecycle. Absent in NArk mode — calls there fault. Bodies are the same
/// anonymous shapes the callers use today (e.g. new { MatchId = id }).
/// </summary>
public sealed class DevApi(ArkadeHeroesClient client)
{
    public Task<object> PayInvoiceAsync(object body) => client.PostAsync<object>("/api/dev/pay-invoice", body);
    public Task<object> TransferAssetAsync(object body) => client.PostAsync<object>("/api/dev/transfer-asset", body);
    public Task<object> StakeEscrowAsync(object body) => client.PostAsync<object>("/api/dev/stake-escrow", body);
    public Task<object> RefundEscrowAsync(object body) => client.PostAsync<object>("/api/dev/refund-escrow", body);
    public Task<object> FundBreedEscrowAsync(object body) => client.PostAsync<object>("/api/dev/fund-breed-escrow", body);
    public Task<object> FundMergeEscrowAsync(object body) => client.PostAsync<object>("/api/dev/fund-merge-escrow", body);
    public Task<object> FundDeathMatchEscrowAsync(object body) => client.PostAsync<object>("/api/dev/fund-deathmatch-escrow", body);
    public Task<object> ReclaimDeathMatchAsync(object body) => client.PostAsync<object>("/api/dev/reclaim-deathmatch", body);
    public Task<object> RefundMergeAsync(object body) => client.PostAsync<object>("/api/dev/refund-merge", body);
    public Task<object> RefundBreedAsync(object body) => client.PostAsync<object>("/api/dev/refund-breed", body);
    public Task<object> FundOfferAsync(object body) => client.PostAsync<object>("/api/dev/fund-offer", body);
    public Task<object> FulfillOfferAsync(object body) => client.PostAsync<object>("/api/dev/fulfill-offer", body);
    public Task<object> ReclaimOfferAsync(object body) => client.PostAsync<object>("/api/dev/reclaim-offer", body);
}
