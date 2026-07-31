using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Core.Assets;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Where a buy puts the buyer's OWN heroes (<see cref="OfferFulfillFlow.BuildFulfillPacket"/>).
/// Once <c>SelectBuyerFunding</c> may spend an asset carrier, the fulfil's asset packet is the
/// only thing standing between the buyer and their heroes: the covenant spend calls the SDK's
/// <c>ConstructArkTransaction</c> directly, which computes no change output and builds no asset
/// packet of its own, so an input asset this packet does not name has nowhere to land. These
/// assert the routing directly rather than only through a live arkd.
/// </summary>
public class OfferFulfillAssetRoutingTests
{
    private const long Dust = 1_000;
    private static readonly string Item = AssetIdHex(0x11);
    private static readonly string HeroA = AssetIdHex(0x22);
    private static readonly string HeroB = AssetIdHex(0x33);

    [Fact]
    public void PureBtcFundingEmitsTheItemOnlyPacket()
    {
        // The shape proven live before the carrier fallback existed — it must not move.
        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [MakeCoin(50_000, vout: 0)], buyerChangeSats: 27_000, Dust);

        var group = Assert.Single(packet.Groups);
        Assert.Equal(Item, group.AssetId!.ToString());
        Assert.Equal([(0, 1UL)], group.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(1, 1UL)], group.Outputs.Select(o => (o.Vout, o.Amount)));
    }

    [Fact]
    public void CarriedHeroesAreRoutedToTheBuyersOwnOutput()
    {
        // The consolidated-wallet case: the buyer pays with the one VTXO that also holds their
        // hero. That hero must be named in the packet and land on the buyer's own output.
        var carrier = MakeCoin(1_118_306, vout: 0, assets: [new VtxoAsset(HeroA, 1)]);

        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [carrier], buyerChangeSats: 1_095_306, Dust);

        var hero = Assert.Single(packet.Groups, g => g.AssetId!.ToString() == HeroA);
        // The offer VTXO is vin 0, so the first funding coin is vin 1.
        Assert.Equal([(1, 1UL)], hero.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(1, 1UL)], hero.Outputs.Select(o => (o.Vout, o.Amount)));

        // ...and the item still reaches the buyer at the same output.
        var item = Assert.Single(packet.Groups, g => g.AssetId!.ToString() == Item);
        Assert.Equal([(0, 1UL)], item.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(1, 1UL)], item.Outputs.Select(o => (o.Vout, o.Amount)));
    }

    [Fact]
    public void EveryAssetOnEveryCarrierIsAccountedFor()
    {
        // Two funding coins, three hero units between them, at the fee-leg's item vout.
        var first = MakeCoin(9_000, vout: 0, assets: [new VtxoAsset(HeroA, 1)]);
        var second = MakeCoin(8_000, vout: 1, assets: [new VtxoAsset(HeroA, 1), new VtxoAsset(HeroB, 5)]);

        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 2, [first, second], buyerChangeSats: 12_000, Dust);

        Assert.Equal(3, packet.Groups.Count);
        var a = Assert.Single(packet.Groups, g => g.AssetId!.ToString() == HeroA);
        Assert.Equal([(1, 1UL), (2, 1UL)], a.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(2, 2UL)], a.Outputs.Select(o => (o.Vout, o.Amount)));

        var b = Assert.Single(packet.Groups, g => g.AssetId!.ToString() == HeroB);
        Assert.Equal([(2, 5UL)], b.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(2, 5UL)], b.Outputs.Select(o => (o.Vout, o.Amount)));
    }

    [Fact]
    public void BuyerAlreadyOwningUnitsOfTheOfferedItemMergesIntoOneGroup()
    {
        // A stackable item the buyer already holds: a second group for the same asset id would
        // trip the packet's duplicate-group rule and fail the buy outright.
        var carrier = MakeCoin(60_000, vout: 0, assets: [new VtxoAsset(Item, 4)]);

        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [carrier], buyerChangeSats: 37_000, Dust);

        var group = Assert.Single(packet.Groups);
        Assert.Equal([(0, 1UL), (1, 4UL)], group.Inputs.Select(i => (i.Vin, i.Amount)));
        Assert.Equal([(1, 5UL)], group.Outputs.Select(o => (o.Vout, o.Amount)));
    }

    [Fact]
    public void SubDustBuyerOutputCarryingHeroesIsRefused()
    {
        // The SDK rewrites a sub-dust P2TR output into an OP_RETURN in place, so this output
        // would destroy the item and the hero riding the funding coin. Refuse the buy instead.
        var carrier = MakeCoin(30_000, vout: 0, assets: [new VtxoAsset(HeroA, 1)]);

        var ex = Assert.Throws<InvalidOperationException>(() => OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [carrier], buyerChangeSats: Dust - 1, Dust));

        Assert.Contains("dust", ex.Message);
    }

    [Fact]
    public void ABuyerOutputAtExactlyDustIsAllowed()
    {
        // The floor is inclusive — dust itself is a real VTXO, so this buy must go through.
        var carrier = MakeCoin(30_000, vout: 0, assets: [new VtxoAsset(HeroA, 1)]);

        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [carrier], buyerChangeSats: Dust, Dust);

        Assert.Equal(2, packet.Groups.Count);
    }

    [Fact]
    public void SubDustBuyerOutputIsNotRefusedWhenNoHeroesRide()
    {
        // The guard keys on the buyer's own assets being at stake, not on the change alone —
        // a pure-BTC fulfil that happens to leave sub-dust change is not this bug's business.
        var packet = OfferFulfillFlow.BuildFulfillPacket(
            Item, itemVout: 1, [MakeCoin(30_000, vout: 0)], buyerChangeSats: Dust - 1, Dust);

        Assert.Single(packet.Groups);
    }

    /// <summary>A valid 34-byte asset id (32B txid + 2B group index) from a single repeated byte.</summary>
    private static string AssetIdHex(byte seed, ushort groupIndex = 0) =>
        AssetId.Create(Convert.ToHexString(Enumerable.Repeat(seed, 32).ToArray()), groupIndex).ToString();

    /// <summary>In-memory ArkCoin (OP_TRUE leaf), same construction as the SDK's ArkCoinTests.</summary>
    private static ArkCoin MakeCoin(long sats, uint vout, IReadOnlyList<VtxoAsset>? assets = null)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var key = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
            .ToOutputDescriptor(Network.RegTest);
        return new ArkCoin(
            walletIdentifier: "buyer-wallet",
            contract: new GenericArkContract(key, [script]),
            birth: DateTimeOffset.UtcNow.AddDays(-2),
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, vout),
            txOut: new TxOut(Money.Satoshis(sats), Script.Empty),
            signerDescriptor: key,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false,
            assets: assets);
    }
}
