using System.Text.Json;
using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions.Extensions;
using NBitcoin;
using NBitcoin.Scripting;
using Xunit;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The same guarantee <see cref="OfferContractAddressStabilityTests"/> pins for offers, swept across
/// the other three covenant builders. Each one's doc comment already states the invariant — "both
/// derive a byte-identical taptree and address from the same params" — because the server funds the
/// escrow and the CLIENT later rebuilds it from stored params to reclaim (BreedEscrowRefundFlow,
/// MergeEscrowRefundFlow, DeathMatchRefundFlow, each fed by an IChainService Get*ParamsAsync).
///
/// So a param that fails to survive storage and the wire strands whatever the escrow holds: staked
/// heroes and fee sats for breed/merge, and BOTH players' heroes, gear and sats for a death match.
/// Until now that invariant was prose plus live E2E coverage only.
/// </summary>
public class EscrowContractAddressStabilityTests
{
    private static readonly string OperatorHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string EmulatorSignerHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";

    private const string PlayerAddress =
        "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63";
    private const string OtherAddress =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nk05rsmrf8h94j26905e7n6sng8y059z8ykn2j5xcuw4xt846qj6x";

    // 34 bytes: 32-byte txid + 2-byte little-endian group index.
    private static string Asset(char fill) => new string(fill, 64) + "0000";
    private const string OraclePkHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string CommitmentHex = "1122334455667788112233445566778811223344556677881122334455667788";
    private const long RefundAfter = 1_800_000_000;

    private static OutputDescriptor Operator() => KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest);
    private static T RoundTrip<T>(T p) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(p))!;

    // ── Breed ──────────────────────────────────────────────────────────────

    private static BreedEscrowParams Breed() => new(
        PlayerAddress, Asset('a'), Asset('b'), Asset('c'), OtherAddress,
        FeeSats: 1_000, EscrowSats: 1_660, OraclePkHex: OraclePkHex,
        BreedingId: "breed-roundtrip", RefundAfterUnixSeconds: RefundAfter);

    private static string AddressOf(BreedEscrowParams p) =>
        BreedEscrowContracts.Build(p, Operator(), EmulatorSignerHex).GetArkAddress().ScriptPubKey.ToHex();

    [Fact]
    public void ABreedEscrow_RebuildsToTheSameAddress_AfterASerializationRoundTrip()
        => Assert.Equal(AddressOf(Breed()), AddressOf(RoundTrip(Breed())));

    [Fact]
    public void ABreedEscrowAddress_MovesWhenItsMoneyParamsMove()
    {
        var baseline = AddressOf(Breed());
        Assert.NotEqual(baseline, AddressOf(Breed() with { FeeSats = 1_001 }));
        Assert.NotEqual(baseline, AddressOf(Breed() with { TreasuryFeeAddress = PlayerAddress }));
        Assert.NotEqual(baseline, AddressOf(Breed() with { PlayerAddress = OtherAddress }));
    }

    // ── Merge ──────────────────────────────────────────────────────────────

    private static MergeEscrowParams Merge() => new(
        PlayerAddress, Asset('d'), Asset('e'), Asset('c'), OtherAddress,
        FeeSats: 1_000, EscrowSats: 1_660, OraclePkHex: OraclePkHex,
        MergeId: "merge-roundtrip", RefundAfterUnixSeconds: RefundAfter);

    private static string AddressOf(MergeEscrowParams p) =>
        MergeEscrowContracts.Build(p, Operator(), EmulatorSignerHex).GetArkAddress().ScriptPubKey.ToHex();

    [Fact]
    public void AMergeEscrow_RebuildsToTheSameAddress_AfterASerializationRoundTrip()
        => Assert.Equal(AddressOf(Merge()), AddressOf(RoundTrip(Merge())));

    [Fact]
    public void AMergeEscrowAddress_MovesWhenItsMoneyParamsMove()
    {
        var baseline = AddressOf(Merge());
        Assert.NotEqual(baseline, AddressOf(Merge() with { FeeSats = 1_001 }));
        Assert.NotEqual(baseline, AddressOf(Merge() with { TreasuryFeeAddress = PlayerAddress }));
    }

    // ── Death match (joint) ────────────────────────────────────────────────

    private static DeathMatchJointEscrowParams Duel(
        IReadOnlyList<GearStake>? challengerGear = null, bool absorb = false) => new(
        PlayerAddress, Asset('1'), OtherAddress, Asset('2'), CommitmentHex, OraclePkHex,
        DeathMatchId: "dm-roundtrip", EscrowSats: 5_000, RefundAfterUnixSeconds: RefundAfter,
        ChallengerGear: challengerGear, DefenderGear: null, Absorb: absorb, SpeciesId: Asset('c'));

    private static string AddressOf(DeathMatchJointEscrowParams p) =>
        DeathMatchEscrowContracts.BuildJoint(p, Operator(), EmulatorSignerHex).GetArkAddress().ScriptPubKey.ToHex();

    [Fact]
    public void ADeathMatchEscrow_RebuildsToTheSameAddress_AfterASerializationRoundTrip()
        => Assert.Equal(AddressOf(Duel()), AddressOf(RoundTrip(Duel())));

    /// <summary>
    /// The riskiest shape of the four: a nested collection behind an interface type. If the gear list
    /// came back reordered, empty, or as a different implementation, the taptree — and so the address
    /// holding both players' staked heroes and items — would move.
    /// </summary>
    [Fact]
    public void ADeathMatchEscrowWithStakedGear_SurvivesTheRoundTripIntact()
    {
        var geared = Duel([new GearStake(Asset('7'), 1, "sword"), new GearStake(Asset('8'), 2, null)]);
        var restored = RoundTrip(geared);

        Assert.Equal(2, restored.ChallengerGear!.Count);
        Assert.Equal(Asset('7'), restored.ChallengerGear[0].AssetId);
        Assert.Equal("sword", restored.ChallengerGear[0].ItemId);
        Assert.Equal(2, restored.ChallengerGear[1].Amount);
        Assert.Null(restored.ChallengerGear[1].ItemId);
        Assert.Equal(AddressOf(geared), AddressOf(restored));
    }

    [Fact]
    public void AbsorbModeIsStructural_SoItChangesTheAddress()
        => Assert.NotEqual(AddressOf(Duel()), AddressOf(Duel(absorb: true)));

    [Fact]
    public void StakedGearIsStructural_SoItChangesTheAddress()
        => Assert.NotEqual(AddressOf(Duel()), AddressOf(Duel([new GearStake(Asset('7'), 1, "sword")])));

    /// <summary>
    /// EscrowSats is deliberately NOT covenant-bound, unlike an offer's ask (which OfferContracts pins
    /// via PayTo). The settle branches sweep whatever the escrow holds to a structurally-pinned winner
    /// script, so the covenant enforces WHO is paid, not HOW MUCH was staked — the amount is a
    /// server-side funding gate before the match starts.
    ///
    /// Pinned so the split is deliberate rather than assumed: if a future change starts pinning the
    /// stake structurally, this fails and the escrow-address contract gets re-examined on purpose.
    /// </summary>
    [Fact]
    public void EscrowSatsIsNotCovenantBound_SoTheAddressIsTheSameWhateverIsStaked()
        => Assert.Equal(AddressOf(Duel()), AddressOf(Duel() with { EscrowSats = 5_001 }));
}
