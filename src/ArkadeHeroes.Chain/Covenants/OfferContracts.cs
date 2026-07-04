using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// A resting item offer's public parameters — everything needed to rebuild the
/// offer covenant. The seller deposits the item asset (plus a little carrier
/// dust) into the offer address; ANYONE may fulfill it by paying the seller the
/// ask in the same transaction (the buyer funds that from their own wallet and
/// takes the item), or the seller reclaims it after expiry. Public — the offer
/// address commits to them — served at <c>/api/offers/{id}</c>.
/// </summary>
public sealed record OfferParams(
    string SellerAddress,
    string ItemAssetId,
    long AskSats,
    long OfferValueSats,
    string OfferId,
    long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of an item-offer covenant — shared by the server
/// (offer listing, fulfilment assembly) and any client, so all derive the same
/// address from the same <see cref="OfferParams"/>.
/// </summary>
public static class OfferContracts
{
    /// <summary>
    /// The offer covenant: a <c>fulfill</c> leaf that lets anyone spend the
    /// offer VTXO ONLY if they pay the seller exactly the ask in the same tx
    /// (banco's non-interactive-swap shape — the emulator refuses underpayment),
    /// and a timelocked <c>reclaim</c> leaf paying the offer value back to the
    /// seller so an unsold offer is recoverable (task 17/18 refund machinery).
    /// </summary>
    public static ArkadeArtifactContract Build(
        OfferParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var sellerScript = ArkAddress.Parse(parameters.SellerAddress).ScriptPubKey;
        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            "item-offer", operatorKey, emulatorSignerKeyHex,
            [
                new("fulfill", ArkadeCovenants.PayTo(sellerScript, parameters.AskSats)),
                new("reclaim", ArkadeCovenants.RefundTo(sellerScript, parameters.OfferValueSats), refundLockTime),
            ]);
    }
}
