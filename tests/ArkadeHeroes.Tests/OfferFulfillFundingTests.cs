using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Deterministic guard for the browser buy path's funding selection
/// (<see cref="OfferFulfillFlow.SelectBuyerFunding"/>): recoverable coins — swept or
/// past expiry — must never be offered as buyer funding, because arkd rejects any
/// spend that includes one with <c>VTXO_RECOVERABLE</c>. Coins are constructed
/// in-memory (same shape as the SDK's ArkCoinTests), no regtest needed.
/// </summary>
public class OfferFulfillFundingTests
{
    private const long Ask = 5_000;

    [Fact]
    public void SweptCoinIsExcludedFromBuyerFunding()
    {
        var swept = MakeCoin(100_000, vout: 0, swept: true);
        var clean = MakeCoin(50_000, vout: 1);

        var (funding, fundedSats) = OfferFulfillFlow.SelectBuyerFunding([swept, clean], Ask);

        Assert.Equal(new[] { 50_000L }, funding.Select(c => c.Amount.Satoshi));
        Assert.Equal(50_000L, fundedSats);
    }

    [Fact]
    public void ExpiredCoinIsExcludedFromBuyerFunding()
    {
        var expired = MakeCoin(80_000, vout: 0, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var clean = MakeCoin(10_000, vout: 1);

        var (funding, fundedSats) = OfferFulfillFlow.SelectBuyerFunding([expired, clean], Ask);

        Assert.Equal(new[] { 10_000L }, funding.Select(c => c.Amount.Satoshi));
        Assert.Equal(10_000L, fundedSats);
    }

    [Fact]
    public void OnlyRecoverableCoinsLeaveTheAskUnfunded()
    {
        // The caller then fails fast with the clear insufficient-funds error instead
        // of submitting a transaction arkd would reject with VTXO_RECOVERABLE.
        var swept = MakeCoin(100_000, vout: 0, swept: true);
        var expired = MakeCoin(80_000, vout: 1, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var (funding, fundedSats) = OfferFulfillFlow.SelectBuyerFunding([swept, expired], Ask);

        Assert.Empty(funding);
        Assert.Equal(0L, fundedSats);
    }

    [Fact]
    public void AssetCarriersExcludedAndLargestCoinsSelectedFirstUntilCovered()
    {
        // Existing selection semantics, unchanged by the recoverable filter.
        var carrier = MakeCoin(1_000_000, vout: 0, assets: [new VtxoAsset("asset:item", 1)]);
        var small = MakeCoin(20_000, vout: 1);
        var mid = MakeCoin(30_000, vout: 2);
        var large = MakeCoin(40_000, vout: 3);

        var (funding, fundedSats) = OfferFulfillFlow.SelectBuyerFunding(
            [carrier, small, mid, large], askSats: 60_000);

        Assert.Equal(new[] { 40_000L, 30_000L }, funding.Select(c => c.Amount.Satoshi));
        Assert.Equal(70_000L, fundedSats);
    }

    /// <summary>In-memory ArkCoin (OP_TRUE leaf), same construction as the SDK's ArkCoinTests.</summary>
    private static ArkCoin MakeCoin(
        long sats, uint vout, bool swept = false, DateTimeOffset? expiresAt = null,
        IReadOnlyList<VtxoAsset>? assets = null)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var key = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
            .ToOutputDescriptor(Network.RegTest);
        return new ArkCoin(
            walletIdentifier: "buyer-wallet",
            contract: new GenericArkContract(key, [script]),
            birth: DateTimeOffset.UtcNow.AddDays(-2),
            expiresAt: expiresAt ?? DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, vout),
            txOut: new TxOut(Money.Satoshis(sats), Script.Empty),
            signerDescriptor: key,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: swept,
            unrolled: false,
            assets: assets);
    }
}
