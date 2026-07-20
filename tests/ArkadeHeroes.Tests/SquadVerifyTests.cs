using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>Trustless replay of a squad match: a faithful SquadReplayDto verifies; any tamper is caught.</summary>
public class SquadVerifyTests
{
    static Hero MakeHero(string id, int level, byte seed)
    {
        var g = new byte[32];
        Array.Fill(g, seed);
        return new Hero { Id = id, OwnerId = "t", Name = id, Genome = Genome.NewGen0(g), Level = level };
    }

    [Fact]
    public void VerifySquad_AcceptsFaithful_RejectsTampered()
    {
        const string matchId = "sq1";
        const string nonce = "nonce";
        var seed = new byte[32];
        Array.Fill(seed, (byte)3);
        var commitment = CommitReveal.Commit(seed);
        var entropy = CommitReveal.DeriveEntropy(seed, "squad", matchId, nonce);

        var challengers = new List<Hero> { MakeHero("c0", 5, 1), MakeHero("c1", 5, 2), MakeHero("c2", 5, 3) };
        var defenders = new List<Hero> { MakeHero("d0", 5, 10), MakeHero("d1", 5, 20), MakeHero("d2", 5, 30) };
        var result = SquadBattle.Resolve(challengers, defenders, entropy);

        var resultDto = new SquadResultDto(result.ChallengerWon, result.ChallengerWins, result.DefenderWins,
            result.Duels.Select(x => new SquadDuelDto(
                x.Slot, challengers[x.Slot].ToDto(), defenders[x.Slot].ToDto(), x.Result.ToDto())).ToList());
        var replay = new SquadReplayDto(
            challengers.Select(h => h.ToDto()).ToList(), defenders.Select(h => h.ToDto()).ToList(),
            resultDto, commitment, Convert.ToHexString(seed), Convert.ToHexString(entropy), nonce);

        Assert.True(FairnessAudit.VerifySquad(matchId, nonce, commitment, replay).Ok);

        // Tamper 1: flip the aggregate winner.
        var flipped = replay with { Result = resultDto with { ChallengerWon = !resultDto.ChallengerWon } };
        Assert.False(FairnessAudit.VerifySquad(matchId, nonce, commitment, flipped).Ok);

        // Tamper 2: alter a duel's reported winner.
        var badDuel = resultDto.Duels[0] with { Result = resultDto.Duels[0].Result with { WinnerId = "phantom" } };
        var badReplay = replay with { Result = resultDto with { Duels = new[] { badDuel }.Concat(resultDto.Duels.Skip(1)).ToList() } };
        Assert.False(FairnessAudit.VerifySquad(matchId, nonce, commitment, badReplay).Ok);
    }
}
