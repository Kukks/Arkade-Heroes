using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>Public because the address commits to them, so a bidder can rebuild and reclaim without the
/// server. <paramref name="FeeSats"/> is absorbed by the OWNER; the bidder pays exactly the bid.</summary>
public sealed record BidEscrowParams(
    string BidderAddress,
    string OwnerAddress,
    string HeroAssetId,
    long BidSats,
    string BidId,
    long RefundAfterUnixSeconds,
    long FeeSats = 0,
    string? TreasuryFeeAddress = null);

/// <summary><see cref="OfferContracts"/> inverted: the BUYER escrows the sats, the owner delivers.</summary>
public static class BidEscrowContracts
{
    /// <summary>
    /// Pinning the hero to the bidder is the one place this must NOT copy <see cref="OfferContracts"/>. An
    /// offer's fulfiller is the buyer, who routes the item to themselves out of self-interest, so presence at
    /// a witness-supplied output suffices there. A bid's fulfiller is the OWNER, who would happily take the
    /// sats and keep the hero.
    /// </summary>
    public static ArkadeArtifactContract Build(
        BidEscrowParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var bidderScript = ArkAddress.Parse(parameters.BidderAddress).ScriptPubKey;
        var ownerScript = ArkAddress.Parse(parameters.OwnerAddress).ScriptPubKey;
        var hero = global::NArk.Core.Assets.AssetId.FromString(parameters.HeroAssetId);

        var fee = string.IsNullOrEmpty(parameters.TreasuryFeeAddress) ? 0 : parameters.FeeSats;
        if (fee < 0 || fee >= parameters.BidSats)
            throw new ArgumentOutOfRangeException(nameof(parameters),
                $"The marketplace fee ({fee}) must be non-negative and below the bid ({parameters.BidSats}).");

        // Witness (bottom→top): [feeOutIdx, ownerOutIdx]; the hero leg is baked (output 0).
        byte[] fulfill = fee > 0
            ?
            [
                .. ArkadeCovenants.PayTo(ownerScript, parameters.BidSats - fee),
                0x69, // OP_VERIFY — PayTo ends in EQUAL
                .. ArkadeCovenants.PayTo(ArkAddress.Parse(parameters.TreasuryFeeAddress!).ScriptPubKey, fee),
                0x69,
                .. ArkadeCovenants.AssetAtOutput(0, hero, bidderScript),
                0x51, // OP_1 — leave exactly one truthy item
            ]
            :
            [
                .. ArkadeCovenants.PayTo(ownerScript, parameters.BidSats),
                0x69,
                .. ArkadeCovenants.AssetAtOutput(0, hero, bidderScript),
                0x51,
            ];

        return new ArkadeArtifactContract(
            "hero-bid", operatorKey, emulatorSignerKeyHex,
            [
                new("fulfill", fulfill),
                new("reclaim", ArkadeCovenants.RefundTo(bidderScript, parameters.BidSats),
                    new LockTime((uint)parameters.RefundAfterUnixSeconds)),
            ]);
    }
}
