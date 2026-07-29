using System.Text.Json;
using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions.Extensions;
using NBitcoin;
using Xunit;

namespace ArkadeHeroes.Tests;

/// <summary>
/// An offer's ADDRESS is the hash of the contract <see cref="OfferContracts.Build"/> emits, so every
/// input to Build is load-bearing forever: the seller reclaims (and the buyer fulfils) by REBUILDING
/// the contract from the offer's stored params and spending the address that falls out. If a param
/// ever failed to survive the trip through storage and the wire, Build would derive a DIFFERENT
/// address and an unsold hero would be unreclaimable — locked in a covenant nobody can reproduce.
///
/// Build was previously exercised only by the live regtest E2Es, which construct their params inline
/// and so never crossed a serialization boundary. These are hermetic: no chain, no emulator.
/// </summary>
public class OfferContractAddressStabilityTests
{
    // Any fixed operator key works — every assertion here compares two builds to each other, never to
    // a golden address, so the key only has to be valid and identical across the pair.
    private static readonly string OperatorHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string EmulatorSignerHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";

    // Real regtest addresses — ArkAddress.Parse is pure bech32m decoding, so these need no node.
    private const string SellerAddress =
        "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63";
    private const string TreasuryAddress =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nk05rsmrf8h94j26905e7n6sng8y059z8ykn2j5xcuw4xt846qj6x";

    // 34 bytes: 32-byte txid + 2-byte little-endian group index.
    private const string ItemAssetId =
        "1111111111111111111111111111111111111111111111111111111111111111" + "0000";

    private const long Ask = 25_000;
    private const long Fee = 1_000;
    private const long RefundAfter = 1_800_000_000;

    private static string AddressOf(OfferParams p) =>
        OfferContracts.Build(p, KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest), EmulatorSignerHex)
            .GetArkAddress().ScriptPubKey.ToHex();

    private static OfferParams WithFee() => new(
        SellerAddress, ItemAssetId, Ask, 330, "offer-roundtrip", RefundAfter,
        FeeSats: Fee, TreasuryFeeAddress: TreasuryAddress);

    private static OfferParams RoundTrip(OfferParams p) =>
        JsonSerializer.Deserialize<OfferParams>(JsonSerializer.Serialize(p))!;

    /// <summary>
    /// The reclaim path (GameClient -> Offers.ParamsAsync -> GetOfferParamsAsync -> Build) only works
    /// because the fee params survive storage and the wire. Pin it.
    /// </summary>
    [Fact]
    public void AFeeBearingOffer_RebuildsToTheSameAddress_AfterASerializationRoundTrip()
    {
        var original = WithFee();
        var restored = RoundTrip(original);

        // Named first so a dropped field fails as "the fee vanished", not as an opaque address mismatch.
        Assert.Equal(Fee, restored.FeeSats);
        Assert.Equal(TreasuryAddress, restored.TreasuryFeeAddress);
        Assert.Equal(AddressOf(original), AddressOf(restored));
    }

    /// <summary>
    /// Teeth for the test above: the fee really is baked into the address, so the round-trip
    /// assertion cannot pass vacuously (e.g. if Build ignored the fee params entirely).
    /// </summary>
    [Fact]
    public void TheFeeIsPartOfTheAddress_SoADifferentFeeIsADifferentContract()
    {
        var withFee = AddressOf(WithFee());
        var withOtherFee = AddressOf(WithFee() with { FeeSats = Fee + 1 });
        var withOtherTreasury = AddressOf(WithFee() with { TreasuryFeeAddress = SellerAddress });

        Assert.NotEqual(withFee, withOtherFee);
        Assert.NotEqual(withFee, withOtherTreasury);
    }

    /// <summary>
    /// The migration guarantee the trailing-optional params exist to provide: an offer stored BEFORE
    /// the fee existed deserializes with FeeSats = 0 / no treasury and must rebuild to the address it
    /// was funded at, or every pre-fee listing becomes unspendable.
    /// </summary>
    [Fact]
    public void AnOfferStoredBeforeTheFeeExisted_RebuildsToItsOriginalAddress()
    {
        // Exactly the six params the record had before the fee was added.
        var preFee = new OfferParams(SellerAddress, ItemAssetId, Ask, 330, "offer-legacy", RefundAfter);

        // A blob written by the OLD server carries only those six keys; today's record fills the rest.
        var legacyJson = JsonSerializer.Serialize(new
        {
            preFee.SellerAddress, preFee.ItemAssetId, preFee.AskSats, preFee.OfferValueSats,
            preFee.OfferId, preFee.RefundAfterUnixSeconds,
        });
        var restored = JsonSerializer.Deserialize<OfferParams>(legacyJson)!;

        Assert.Equal(0, restored.FeeSats);
        Assert.Null(restored.TreasuryFeeAddress);
        Assert.Equal(AddressOf(preFee), AddressOf(restored));
    }

    /// <summary>
    /// A configured fee with no treasury address is treated as no fee at all (Build's own guard), and
    /// must not perturb the address — otherwise flipping the fee config would strand live offers.
    /// </summary>
    [Fact]
    public void AFeeWithoutATreasuryAddress_IsInertAndLeavesTheAddressUnchanged()
    {
        var noFee = new OfferParams(SellerAddress, ItemAssetId, Ask, 330, "offer-legacy", RefundAfter);
        var feeButNoTreasury = noFee with { FeeSats = Fee, TreasuryFeeAddress = null };

        Assert.Equal(AddressOf(noFee), AddressOf(feeButNoTreasury));
    }

    /// <summary>
    /// The fee changes the FULFILL leaf and nothing else: reclaim is built from the item, the seller
    /// script and the timelock, none of which a fee touches, and both variants carry the same two
    /// leaves. That is what lets the existing live reclaim probe — which only ever ran a no-fee
    /// contract — stand for the fee-bearing case too: a seller reclaiming a fee-bearing offer spends a
    /// leaf byte-identical to the one already proven to co-sign, through a control block of identical
    /// shape, at an address the round-trip facts above pin.
    ///
    /// Pinned so that stays true. If someone makes reclaim fee-dependent, the composition argument
    /// silently stops holding and only this test would notice.
    /// </summary>
    [Fact]
    public void TheFeeChangesOnlyTheFulfillLeaf_LeavingReclaimByteIdentical()
    {
        var noFee = new OfferParams(SellerAddress, ItemAssetId, Ask, 330, "offer-leaves", RefundAfter);
        var withFee = noFee with { FeeSats = Fee, TreasuryFeeAddress = TreasuryAddress };

        var a = OfferContracts.Build(noFee, KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest), EmulatorSignerHex);
        var b = OfferContracts.Build(withFee, KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest), EmulatorSignerHex);

        Assert.Equal(a.ScriptFor("reclaim"), b.ScriptFor("reclaim"));
        Assert.NotEqual(a.ScriptFor("fulfill"), b.ScriptFor("fulfill"));
        Assert.Equal(a.FunctionNames.OrderBy(n => n), b.FunctionNames.OrderBy(n => n));
    }
}
