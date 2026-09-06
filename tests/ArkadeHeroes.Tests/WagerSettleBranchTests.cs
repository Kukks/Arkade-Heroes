using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The wager escrow's sibling of <see cref="DeathMatchDefenderBranchTests"/>. Both parties' contracts
/// share ONE settle-branch array, so a swap here is not address-distinguishable: pointing
/// <c>settleToDefender</c> at the challenger's script sweeps the pot to the player who LOST. The refund
/// leaves need no cover — they are what separates the two contracts, so a swap collapses their addresses.
/// </summary>
public class WagerSettleBranchTests
{
    private const string EmulatorSignerHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string OperatorHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string OraclePkHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string CommitmentHex = "1122334455667788112233445566778811223344556677881122334455667788";
    private const string MatchId = "match-defender-branch";

    private const string ChallengerAddress =
        "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63";
    private const string DefenderAddress =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nk05rsmrf8h94j26905e7n6sng8y059z8ykn2j5xcuw4xt846qj6x";

    private static ArkadeArtifactContract ChallengerEscrow() => WagerEscrowContracts.Build(
        new WagerEscrowParams(
            CommitmentHex, ChallengerAddress, DefenderAddress, StakeSats: 10_000,
            OraclePkHex: OraclePkHex, MatchId: MatchId, RefundAfterUnixSeconds: 1_800_000_000),
        KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest),
        EmulatorSignerHex).Challenger;

    private static string PayeeProgram(string arkAddress) =>
        Convert.ToHexString(ArkAddress.Parse(arkAddress).ScriptPubKey.ToBytes()[2..]).ToLowerInvariant();

    private static string Leaf(string function) =>
        Convert.ToHexString(ChallengerEscrow().ScriptFor(function)).ToLowerInvariant();

    [Fact]
    public void EachSettleBranchSweepsThePotToItsOwnWinner()
    {
        var toChallenger = Leaf("settleToChallenger");
        var toDefender = Leaf("settleToDefender");

        Assert.Contains(PayeeProgram(ChallengerAddress), toChallenger);
        Assert.DoesNotContain(PayeeProgram(DefenderAddress), toChallenger);
        Assert.Contains(PayeeProgram(DefenderAddress), toDefender);
        Assert.DoesNotContain(PayeeProgram(ChallengerAddress), toDefender);
    }

    [Fact]
    public void TheTwoSidesGateOnDifferentOracleMessages()
    {
        var toDefender = Convert.ToHexString(
            ArkadeCovenants.SettleMessage(MatchId, challengerWon: false)).ToLowerInvariant();

        Assert.Contains(toDefender, Leaf("settleToDefender"));
        Assert.DoesNotContain(toDefender, Leaf("settleToChallenger"));
    }
}
