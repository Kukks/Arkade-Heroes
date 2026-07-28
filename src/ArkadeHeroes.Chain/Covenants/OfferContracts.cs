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
/// <param name="FeeSats">The marketplace fee the covenant routes to <paramref name="TreasuryFeeAddress"/>
/// out of the sale proceeds — the SELLER absorbs it, so a buyer pays exactly <paramref name="AskSats"/>
/// and the seller receives <c>AskSats − FeeSats</c>. 0 disables the fee leg entirely.</param>
/// <param name="TreasuryFeeAddress">Where the enforced fee lands. Null/empty disables the fee leg.</param>
public sealed record OfferParams(
    string SellerAddress,
    string ItemAssetId,
    long AskSats,
    long OfferValueSats,
    string OfferId,
    long RefundAfterUnixSeconds,
    // Trailing + optional so an offer stored BEFORE the fee existed deserializes with FeeSats = 0 and
    // rebuilds to a byte-identical contract (and so the same address) — see OfferContracts.Build.
    long FeeSats = 0,
    string? TreasuryFeeAddress = null);

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

        // The marketplace fee is enforced BY THE COVENANT, out of the sale proceeds: the buyer pays the
        // ask across two pinned outputs — seller gets ask − fee, treasury gets fee. So there is no
        // separate fee payment for a seller to make, to fail, or to strand a listing on, and the server
        // cannot skip or misdirect the cut. This mirrors the breed and merge escrows, which have always
        // routed their treasury fee structurally (see BreedEscrowContracts' BreedRetainAuthorized).
        // A zero fee (or no treasury address) emits the ORIGINAL single-payout leaf byte for byte, so
        // offers created before the fee existed rebuild to the same address and stay spendable.
        var fee = string.IsNullOrEmpty(parameters.TreasuryFeeAddress) ? 0 : parameters.FeeSats;
        if (fee < 0 || fee >= parameters.AskSats)
            throw new ArgumentOutOfRangeException(nameof(parameters),
                $"The marketplace fee ({fee}) must be non-negative and below the ask ({parameters.AskSats}).");

        byte[] fulfill = fee > 0
            ?
            [
                // Witness (bottom→top): [itemOutIdx, feeOutIdx, sellerOutIdx] — each PayTo's leading DUP
                // consumes the top index, then AssetAtWitnessOutput consumes the item index beneath.
                .. ArkadeCovenants.PayTo(sellerScript, parameters.AskSats - fee),
                0x69, // OP_VERIFY — PayTo ends in EQUAL
                .. ArkadeCovenants.PayTo(ArkAddress.Parse(parameters.TreasuryFeeAddress!).ScriptPubKey, fee),
                0x69, // OP_VERIFY
                .. ArkadeCovenants.AssetAtWitnessOutput(item),
                0x51, // OP_1 — leave exactly one truthy item
            ]
            :
            [
                // Witness (bottom→top): [itemOutIdx, payToOutIdx] — PayTo's leading DUP
                // consumes the top; AssetAtWitnessOutput consumes the item index beneath.
                .. ArkadeCovenants.PayTo(sellerScript, parameters.AskSats),
                0x69, // OP_VERIFY — PayTo ends in EQUAL
                .. ArkadeCovenants.AssetAtWitnessOutput(item),
                0x51, // OP_1 — leave exactly one truthy item
            ];

        return new ArkadeArtifactContract(
            "item-offer", operatorKey, emulatorSignerKeyHex,
            [
                new("fulfill", fulfill),
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
