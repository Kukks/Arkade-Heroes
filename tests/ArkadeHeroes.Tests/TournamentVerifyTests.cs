using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>Trustless replay of a tournament bracket — the highest-stakes flow (real-sats buy-ins -> pot ->
/// podium). A faithful TournamentReplayDto verifies; any tamper (champion, a fought-match winner, a
/// substituted entrant, the nonce, a broken commitment) is caught. Mirrors SquadVerifyTests; closes the one
/// resolvable outcome a client previously could not recompute.</summary>
public class TournamentVerifyTests
{
    static Hero MakeHero(string id, int level, byte seed)
    {
        var g = new byte[32];
        Array.Fill(g, seed);
        return new Hero { Id = id, OwnerId = "t", Name = id, Genome = Genome.NewGen0(g), Level = level };
    }

    // 6 entrants -> the bracket has a bye (round 1: 3 alive -> 1 fought + 1 bye), exercising the bye-filter path.
    static List<Hero> Entrants() =>
        Enumerable.Range(0, 6).Select(i => MakeHero($"e{i}", 5, (byte)(i * 7 + 1))).ToList();

    static TournamentReplayDto BuildReplay(string id, string nonce, byte[] seed, List<Hero> entrants, out string commitment)
    {
        commitment = CommitReveal.Commit(seed);
        var entropy = CommitReveal.DeriveEntropy(seed, "tournament", id, nonce);
        var result = Tournament.Resolve(entrants, entropy);
        var bracket = result.Matches.Where(m => m.Result is not null)
            .Select(m => new TournamentMatchDto(m.Round, m.Index, m.AId, m.BId, m.WinnerId)).ToList();
        return new TournamentReplayDto(
            entrants.Select(h => h.ToDto()).ToList(), bracket, result.ChampionId,
            commitment, Convert.ToHexString(seed), Convert.ToHexString(entropy), nonce);
    }

    [Fact]
    public void VerifyTournament_AcceptsFaithful_RejectsTampered()
    {
        const string id = "t1";
        const string nonce = "nonce";
        var seed = new byte[32];
        Array.Fill(seed, (byte)5);
        var replay = BuildReplay(id, nonce, seed, Entrants(), out var commitment);

        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, replay).Ok);

        // Tamper 1: claim a different champion took the pot.
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment,
            replay with { ChampionHeroId = "phantom" }).Ok);

        // Tamper 2: flip a fought match's winner.
        var badBracket = replay.Bracket.ToList();
        badBracket[0] = badBracket[0] with { WinnerId = "phantom" };
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment,
            replay with { Bracket = badBracket }).Ok);

        // Tamper 3: substitute a ringer for an entrant — slot 0 always fights round 0, so it must appear in
        // the bracket, and its new id can't match the reported bracket's.
        var badEntrants = replay.Entrants.ToList();
        badEntrants[0] = MakeHero("ringer", 50, 250).ToDto();
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment,
            replay with { Entrants = badEntrants }).Ok);

        // Tamper 4: verify under a different nonce than the bracket was drawn with.
        Assert.False(FairnessAudit.VerifyTournament(id, "wrong-nonce", commitment, replay).Ok);
    }

    [Fact]
    public void VerifyTournament_RejectsASeedThatBreaksTheCommitment()
    {
        const string id = "t2";
        const string nonce = "n";
        var seed = new byte[32];
        Array.Fill(seed, (byte)9);
        var replay = BuildReplay(id, nonce, seed, Entrants(), out _);
        var commitToOtherSeed = CommitReveal.Commit(new byte[32]);
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitToOtherSeed, replay).Ok);
    }
}
