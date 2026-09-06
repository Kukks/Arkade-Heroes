using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Tests;

/// <summary>
/// No test spends either "defender wins" branch, though <c>NArkChainService</c> picks them whenever
/// <c>challengerWon == false</c>. Both pairs come from one helper with the parties swapped at the call
/// site, so an argument slip there burns both heroes and mints the replacement to the LOSER.
/// STRUCTURAL only: proves each branch is wired to its own winner, not that it is spendable on-chain.
/// </summary>
public class DeathMatchDefenderBranchTests
{
    private const string EmulatorSignerHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string OperatorHex = "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string OraclePkHex = "aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88";
    private const string CommitmentHex = "1122334455667788112233445566778811223344556677881122334455667788";

    private const string ChallengerAddress =
        "tark1qz4d2t2czchfaml2l3ad3gwde2qxpd0srhc7wkpnvtg99cnxyz8c3pnvvhnhumhwhqthmlxmdryakwx99s6508y8dunj9sty2p5mr7unh5re63";
    private const string DefenderAddress =
        "tark1qqellv77udfmr20tun8dvju5vgudpf9vxe8jwhthrkn26fz96pawqfdy8nk05rsmrf8h94j26905e7n6sng8y059z8ykn2j5xcuw4xt846qj6x";

    private static string Asset(char fill) => new string(fill, 64) + "0000";

    private static ArkadeArtifactContract Contract(bool absorb) => DeathMatchEscrowContracts.BuildJoint(
        new DeathMatchJointEscrowParams(
            ChallengerAddress, Asset('1'), DefenderAddress, Asset('2'), CommitmentHex, OraclePkHex,
            DeathMatchId: "dm-defender-branch", EscrowSats: 5_000, RefundAfterUnixSeconds: 1_800_000_000,
            ChallengerGear: null, DefenderGear: null, Absorb: absorb, SpeciesId: Asset('c')),
        KeyExtensions.ParseOutputDescriptor(OperatorHex, Network.RegTest),
        EmulatorSignerHex);

    /// <summary>The 32-byte taproot output key the mint/route checks bake in — the payee's identity.</summary>
    private static string PayeeProgram(string arkAddress) =>
        Convert.ToHexString(ArkAddress.Parse(arkAddress).ScriptPubKey.ToBytes()[2..]).ToLowerInvariant();

    private static string Leaf(ArkadeArtifactContract c, string function) =>
        Convert.ToHexString(c.ScriptFor(function)).ToLowerInvariant();

    private static void RoutesToItsOwnWinner(ArkadeArtifactContract c, string function, string winner, string loser)
    {
        var leaf = Leaf(c, function);
        Assert.Contains(PayeeProgram(winner), leaf);
        Assert.DoesNotContain(PayeeProgram(loser), leaf);
    }

    [Fact]
    public void TheClassicSettleBranches_EachPayTheirOwnWinner()
    {
        var c = Contract(absorb: false);

        RoutesToItsOwnWinner(c, "settleToChallenger", ChallengerAddress, DefenderAddress);
        RoutesToItsOwnWinner(c, "settleToDefender", DefenderAddress, ChallengerAddress);
    }

    [Fact]
    public void TheAbsorbMintBranches_EachMintToTheirOwnWinner()
    {
        var c = Contract(absorb: true);

        RoutesToItsOwnWinner(c, "settleMintChallenger", ChallengerAddress, DefenderAddress);
        RoutesToItsOwnWinner(c, "settleMintDefender", DefenderAddress, ChallengerAddress);
    }

    /// <summary>Sharing a message would let a signature authorising one winner settle for the other.</summary>
    [Fact]
    public void TheTwoSidesGateOnDifferentOracleMessages()
    {
        var c = Contract(absorb: true);
        var settle = Convert.ToHexString(
            ArkadeCovenants.DeathMatchSettleMessage("dm-defender-branch", challengerWon: false)).ToLowerInvariant();
        var mint = Convert.ToHexString(
            ArkadeCovenants.DeathMatchAbsorbMintMessage("dm-defender-branch", challengerWon: false)).ToLowerInvariant();

        Assert.Contains(settle, Leaf(c, "settleToDefender"));
        Assert.DoesNotContain(settle, Leaf(c, "settleToChallenger"));
        Assert.Contains(mint, Leaf(c, "settleMintDefender"));
        Assert.DoesNotContain(mint, Leaf(c, "settleMintChallenger"));
    }
}
