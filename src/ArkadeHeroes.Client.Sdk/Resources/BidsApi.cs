using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>
/// Bids — buying a hero that is NOT for sale: propose → the hero's owner consents (which is what bills the
/// bidder) → the owner sends the hero from their wallet → settle. The mirror of <see cref="OffersApi"/>,
/// which only ever runs owner → market.
///
/// <para>Without <see cref="AcceptAsync"/> nothing is billed and nothing moves, so a bid the owner ignores
/// or refuses costs the bidder nothing at all. Past acceptance, <see cref="RefundAsync"/> is the exit: an
/// owner who takes the money's word for it and never delivers cannot keep the bidder's sats.</para>
/// </summary>
public sealed class BidsApi(ArkadeHeroesClient client)
{
    /// <summary>Every live bid this arena has seen (newest first) — the discovery path a browser needs to
    /// spot an incoming bid on one of its own heroes, and to track its own.</summary>
    public Task<List<BidDto>> ListAsync() => client.GetAsync<List<BidDto>>("/api/bids");

    /// <summary>Offers a hero's owner sats for it. Bills nothing: this is an offer, not a payment.</summary>
    public Task<BidDto> PlaceAsync(PlaceBidRequest req) => client.PostAsync<BidDto>("/api/bids", req);

    /// <summary>The owner's consent — returns the invoice the BIDDER must pay. The only place a bid is
    /// ever billed.</summary>
    public Task<BidInvoiceResponse> AcceptAsync(string bidId) =>
        client.PostAsync<BidInvoiceResponse>($"/api/bids/{bidId}/accept");

    /// <summary>Re-reads what an accepted bid bills, and whether it is FUNDED — how the BIDDER learns their
    /// invoice (the accept response goes to the owner), and how the OWNER learns the money arrived before
    /// sending the hero.</summary>
    public Task<BidInvoiceResponse> InvoiceAsync(string bidId) =>
        client.GetAsync<BidInvoiceResponse>($"/api/bids/{bidId}/invoice");

    /// <summary>The owner's refusal. Terminal, and only possible before consent.</summary>
    public Task<BidDto> DeclineAsync(string bidId) => client.PostAsync<BidDto>($"/api/bids/{bidId}/decline");

    /// <summary>The bidder's retraction. Terminal, and only possible before consent.</summary>
    public Task<BidDto> WithdrawAsync(string bidId) => client.PostAsync<BidDto>($"/api/bids/{bidId}/withdraw");

    /// <summary>Closes a funded, DELIVERED bid: the owner is paid and the hero's record follows the asset.
    /// Callable by either party. Refused until the chain shows the bidder holding the hero.</summary>
    public Task<HeroDto> SettleAsync(string bidId) => client.PostAsync<HeroDto>($"/api/bids/{bidId}/settle");

    /// <summary>Unwinds an accepted bid the owner never delivered against, sending the bidder's sats home.
    /// Either party, and only past the bid's reclaim window.</summary>
    public Task<BidRefundResponse> RefundAsync(string bidId) =>
        client.PostAsync<BidRefundResponse>($"/api/bids/{bidId}/refund");
}
