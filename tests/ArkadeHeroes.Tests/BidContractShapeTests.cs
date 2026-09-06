using System.Text.Json;
using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NBitcoin;
using Xunit;

namespace ArkadeHeroes.Tests;

/// <summary>The address is the hash of what Build emits, so every input is load-bearing forever — the
/// bidder reclaims by REBUILDING from the stored params. Hermetic.</summary>
public class BidContractShapeTests
{
    private static readonly string OperatorHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string EmulatorSignerHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";

    private const string BidderAddress =
        "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63";
    private const string OwnerAddress =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nk05rsmrf8h94j26905e7n6sng8y059z8ykn2j5xcuw4xt846qj6x";
    // Reusing the OWNER's makes the pin STRONGER: the bidder's script then reaches the fulfil leaf only
    // through the hero pin.
    private const string TreasuryAddress = OwnerAddress;

    private const string HeroAssetId =
        "1111111111111111111111111111111111111111111111111111111111111111" + "0000";

    private const long Bid = 25_000;
    private const long Fee = 1_000;
    private const long RefundAfter = 1_800_000_000;

    private static ArkadeArtifactContract Contract(BidEscrowParams p) =>
        BidEscrowContracts.Build(p, KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest), EmulatorSignerHex);

    private static string AddressOf(BidEscrowParams p) =>
        Contract(p).GetArkAddress().ScriptPubKey.ToHex();

    private static BidEscrowParams WithFee() => new(
        BidderAddress, OwnerAddress, HeroAssetId, Bid, "bid-roundtrip", RefundAfter,
        FeeSats: Fee, TreasuryFeeAddress: TreasuryAddress);

    [Fact]
    public void ParamsSurviveAJsonRoundTrip()
    {
        var p = WithFee();
        var back = JsonSerializer.Deserialize<BidEscrowParams>(JsonSerializer.Serialize(p))!;
        Assert.Equal(AddressOf(p), AddressOf(back));
    }

    [Theory]
    [InlineData("bidder")]
    [InlineData("owner")]
    [InlineData("hero")]
    [InlineData("bid")]
    [InlineData("expiry")]
    [InlineData("fee")]
    [InlineData("treasury")]
    public void EveryParamIsLoadBearing(string param)
    {
        var p = WithFee();
        var moved = param switch
        {
            "bidder" => p with { BidderAddress = OwnerAddress },
            "owner" => p with { OwnerAddress = BidderAddress },
            "hero" => p with { HeroAssetId = HeroAssetId.Replace("1111", "2222") },
            "bid" => p with { BidSats = Bid + 1 },
            "expiry" => p with { RefundAfterUnixSeconds = RefundAfter + 1 },
            "fee" => p with { FeeSats = Fee + 1 },
            "treasury" => p with { TreasuryFeeAddress = BidderAddress },
            _ => throw new ArgumentOutOfRangeException(nameof(param)),
        };
        Assert.NotEqual(AddressOf(p), AddressOf(moved));
    }

    /// <summary>The theft guard: see BidEscrowContracts.Build for why the destination must be baked.</summary>
    [Fact]
    public void TheFulfilLeafPinsTheHeroToTheBidder()
    {
        var fulfil = Contract(WithFee()).ScriptFor("fulfill");
        var bidderScript = ArkAddress.Parse(BidderAddress).ScriptPubKey.ToBytes();

        Assert.Contains(Convert.ToHexString(bidderScript[2..]),
            Convert.ToHexString(fulfil));
    }

    [Fact]
    public void MovingTheBidder_MovesWhereTheHeroMustLand()
    {
        // The pin above would be worth nothing if the leaf ignored which bidder it was built for.
        var mine = Contract(WithFee()).ScriptFor("fulfill");
        var theirs = Contract(WithFee() with { BidderAddress = OwnerAddress }).ScriptFor("fulfill");
        Assert.NotEqual(Convert.ToHexString(mine), Convert.ToHexString(theirs));
    }

    [Fact]
    public void TheReclaimLeafPaysTheBidderAndNobodyElse()
    {
        var reclaim = Contract(WithFee()).ScriptFor("reclaim");
        var bidderScript = ArkAddress.Parse(BidderAddress).ScriptPubKey.ToBytes();
        var ownerScript = ArkAddress.Parse(OwnerAddress).ScriptPubKey.ToBytes();

        Assert.Contains(Convert.ToHexString(bidderScript[2..]), Convert.ToHexString(reclaim));
        Assert.DoesNotContain(Convert.ToHexString(ownerScript[2..]), Convert.ToHexString(reclaim));
    }

    [Theory]
    [InlineData(Bid)]
    [InlineData(Bid + 1)]
    [InlineData(-1)]
    public void AFeeThatWouldSwallowTheBid_IsRefused(long fee)
    {
        var p = WithFee() with { FeeSats = fee };
        Assert.Throws<ArgumentOutOfRangeException>(() => AddressOf(p));
    }

    [Fact]
    public void AZeroFeeDropsTheTreasuryLegEntirely()
    {
        var free = WithFee() with { FeeSats = 0, TreasuryFeeAddress = null };

        // One fewer PayTo leg, and a different address — a fee-free bid is a different covenant, not the
        // same one with a zero in it.
        Assert.True(Contract(free).ScriptFor("fulfill").Length < Contract(WithFee()).ScriptFor("fulfill").Length);
        Assert.NotEqual(AddressOf(WithFee()), AddressOf(free));
    }
}
