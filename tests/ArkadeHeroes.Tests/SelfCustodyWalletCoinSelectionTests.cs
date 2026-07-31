using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Which coins the CONSOLE wallet reaches for. The same rules the browser wallet is held to in
/// ArkadeHeroes.Tests.Web's WalletCoinSelectionTests, asserted against the other implementation.
///
/// <para>This is the one piece of the wallet that is pure logic, and it is also the piece where a
/// mistake is unrecoverable: the coins carry the player's heroes, so a spend that picks the wrong
/// one hands a hero to whoever the sats were going to. Selection either returns the exact inputs
/// that get signed, or refuses — there is no third outcome worth having.</para>
/// </summary>
public class SelfCustodyWalletCoinSelectionTests
{
    private const long Dust = 1_000;
    private const string HeroAsset = "hero-1";
    private const string SecondHero = "hero-2";

    // ── Paying a fee at all ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PureBtcCoinIsPreferred_AndTheHeroCarrierIsLeftAlone()
    {
        var carrier = Coin(50_000, HeroAsset);
        var cash = Coin(20_000);

        Assert.Equal([cash], SelfCustodyWallet.SelectFrom([carrier, cash], [Sats(5_000)], Dust));
    }

    [Fact]
    public void NoPureBtcCoinLeft_SpendsTheCarrier()
    {
        // The state a settlement batch leaves: one VTXO holding the sats AND the heroes. Funding sats
        // only from strictly pure-BTC coins meant no fee could ever be paid again from here, while the
        // balance still read the full amount.
        var carrier = Coin(50_000, HeroAsset);

        Assert.Equal([carrier], SelfCustodyWallet.SelectFrom([carrier], [Sats(5_000)], Dust));
    }

    [Fact]
    public void CarrierWhoseChangeWouldFallBelowDust_IsRefused()
    {
        // 5,400 sats against a 5,000 fee leaves 400 — under dust. The SDK would move that change to an
        // OP_RETURN at vout 0 while the asset packet still assigns asset change to the LAST output, so
        // the hero would land on the fee recipient. Refuse the spend instead.
        var carrier = Coin(5_400, HeroAsset);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SelfCustodyWallet.SelectFrom([carrier], [Sats(5_000)], Dust));

        Assert.Contains("spendable sats", ex.Message);
    }

    [Fact]
    public void CarrierLeavingExactlyDustAsChange_IsSpent()
    {
        // The boundary the guard is written against: change == dust is a real VTXO, so it is allowed,
        // and the hero rides home on it.
        var carrier = Coin(6_000, HeroAsset);

        Assert.Equal([carrier], SelfCustodyWallet.SelectFrom([carrier], [Sats(5_000)], Dust));
    }

    // ── Not grabbing a coin the spend does not need ──────────────────────────────────────────────

    [Fact]
    public void OneCoinThatAlreadyClearsDust_DoesNotDragInASpare()
    {
        // The spare used to be taken unconditionally "for headroom". It is not free: the SDK puts a
        // one-MINUTE lock on every input before submitting and never lifts it on failure, so a coin the
        // spend did not need comes back locked — and that is the coin a retry reaches for.
        var big = Coin(50_000);
        var spare = Coin(40_000);

        Assert.Equal([big], SelfCustodyWallet.SelectFrom([big, spare], [Sats(5_000)], Dust));
    }

    [Fact]
    public void BtcCoinsAreTakenUntilTheChangeClearsDust_NotJustUntilTheOutputsAreCovered()
    {
        // 5,400 covers the 5,000 fee but would leave 400 of change — under dust. Sub-dust change is
        // exactly what misplaces asset change, so selection keeps going rather than stopping at
        // "covered".
        var small = Coin(5_400);
        var second = Coin(3_000);

        Assert.Equal(2, SelfCustodyWallet.SelectFrom([small, second], [Sats(5_000)], Dust).Length);
    }

    // ── Not giving away a hero riding on the same coin ───────────────────────────────────────────

    [Fact]
    public void SendingOneHeroOffACoinCarryingTwo_IsRefusedWhenTheChangeWouldNotSurvive()
    {
        // 1,500 sats and two heroes on one coin — the shape a settlement round consolidates a wallet
        // into. Sending one hero costs dust (1,000), leaving 500 of change: under dust, so the SDK
        // moves it to vout 0 while the asset packet still assigns the SECOND hero to the last output.
        // That last output is the recipient.
        var carrier = Coin(1_500, HeroAsset, SecondHero);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SelfCustodyWallet.SelectFrom([carrier], [Asset(HeroAsset)], Dust));

        Assert.Contains("asset change", ex.Message);
    }

    [Fact]
    public void SendingOneUnitOffACoinCarryingTwoOfTheSameAsset_IsRefusedWhenTheChangeWouldNotSurvive()
    {
        // Same defect reached through stacked units rather than two distinct assets.
        var carrier = Coin(1_500, units: 2, assetIds: HeroAsset);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SelfCustodyWallet.SelectFrom([carrier], [Asset(HeroAsset)], Dust));

        Assert.Contains("asset change", ex.Message);
    }

    [Fact]
    public void SendingOneHeroOffACoinCarryingTwo_GoesThroughOnceTheChangeClearsDust()
    {
        // The same send with room to leave a real change output: allowed, and the second hero rides it.
        var carrier = Coin(9_000, HeroAsset, SecondHero);

        Assert.Equal([carrier], SelfCustodyWallet.SelectFrom([carrier], [Asset(HeroAsset)], Dust));
    }

    [Fact]
    public void SendingTheLastHeroOffACoin_IsNotBlockedByTheGuard()
    {
        // Nothing is left behind, so there is no asset change to misplace — a thin carrier is fine.
        // Over-refusing here would strand a hero just as surely as under-refusing gives one away.
        var carrier = Coin(1_500, HeroAsset);

        Assert.Equal([carrier], SelfCustodyWallet.SelectFrom([carrier], [Asset(HeroAsset)], Dust));
    }

    [Fact]
    public void AHeroSendFundedByASeparateBtcCoin_LeavesTheChangeRoomToCarryTheOtherHero()
    {
        // The carrier is thin but a pure-BTC coin pays the dust output, so the change output is fat
        // enough to hold the hero that stays behind.
        var carrier = Coin(1_500, HeroAsset, SecondHero);
        var cash = Coin(20_000);

        var selected = SelfCustodyWallet.SelectFrom([carrier, cash], [Asset(HeroAsset)], Dust);

        Assert.Contains(carrier, selected);
        Assert.Contains(cash, selected);
    }

    [Fact]
    public void AWalletWithoutTheAsset_IsRefusedBeforeAnythingElse()
    {
        var cash = Coin(20_000);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SelfCustodyWallet.SelectFrom([cash], [Asset(HeroAsset)], Dust));

        Assert.Contains(HeroAsset, ex.Message);
    }

    [Fact]
    public void NotEnoughSats_IsRefused()
    {
        var cash = Coin(2_000);

        var ex = Assert.Throws<InvalidOperationException>(
            () => SelfCustodyWallet.SelectFrom([cash], [Sats(5_000)], Dust));

        Assert.Contains("spendable sats", ex.Message);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static ArkTxOut Sats(long amount) =>
        new(ArkTxOutType.Vtxo, Money.Satoshis(amount), Destination());

    private static ArkTxOut Asset(string assetId, ulong amount = 1) =>
        new(ArkTxOutType.Vtxo, Money.Satoshis(Dust), Destination())
        {
            Assets = [new ArkTxOutAsset(assetId, amount)],
        };

    private static uint _index;

    private static ArkCoin Coin(long sats, params string[] assetIds) => Coin(sats, 1, assetIds);

    private static ArkCoin Coin(long sats, ulong units, params string[] assetIds)
    {
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(serverKey.ToOutputDescriptor(Network.RegTest), [script]);
        return new ArkCoin(
            walletIdentifier: "w1",
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, _index++),
            txOut: new TxOut(Money.Satoshis(sats), Script.Empty),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: false,
            unrolled: false,
            assets: assetIds.Length == 0
                ? null
                : [.. assetIds.Select(id => new VtxoAsset(id, units))]);
    }

    private static ArkAddress Destination() =>
        new GenericArkContract(
                ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
                    .ToOutputDescriptor(Network.RegTest),
                [new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE])])
            .GetArkAddress();
}
