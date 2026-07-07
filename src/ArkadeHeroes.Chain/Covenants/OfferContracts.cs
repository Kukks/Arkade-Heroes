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
    /// The offer covenant (covenant-v2, FULLY structural — no oracle): a <c>fulfill</c> leaf
    /// that lets anyone spend the offer VTXO ONLY if they pay the seller exactly the ask AND
    /// the item is CONSERVED — present (amount 1) at a witness-supplied output (the `.ark`
    /// spec's <c>tx.outputs[i].assets.lookup == 1</c> intent; the fulfiller routes it to
    /// themselves out of self-interest, the covenant forbids destruction) — and a timelocked
    /// <c>reclaim</c> leaf that routes the ITEM home to the SELLER at output 0, script-pinned
    /// (closing the theft where anyone post-expiry paid the seller dust while the packet
    /// routed the item to themselves).
    /// </summary>
    public static ArkadeArtifactContract Build(
        OfferParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var sellerScript = ArkAddress.Parse(parameters.SellerAddress).ScriptPubKey;
        var item = global::NArk.Core.Assets.AssetId.FromString(parameters.ItemAssetId);
        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            "item-offer", operatorKey, emulatorSignerKeyHex,
            [
                // Witness (bottom→top): [itemOutIdx, payToOutIdx] — PayTo's leading DUP
                // consumes the top; AssetAtWitnessOutput consumes the item index beneath.
                new("fulfill",
                    [
                        .. ArkadeCovenants.PayTo(sellerScript, parameters.AskSats),
                        0x69, // OP_VERIFY — PayTo ends in EQUAL
                        .. ArkadeCovenants.AssetAtWitnessOutput(item),
                        0x51, // OP_1 — leave exactly one truthy item
                    ]),
                // Fully baked (empty witness); the carrier dust rides the output (not
                // separately pinned — the merge/breed refund precedent, dust-floor bounded).
                new("reclaim",
                    [
                        .. ArkadeCovenants.AssetAtOutput(0, item, sellerScript),
                        0x51, // OP_1
                    ],
                    refundLockTime),
            ]);
    }
}
