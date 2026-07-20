using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Shared;

/// <summary>
/// Client-side verification of server outcomes. Because breeding and combat
/// are pure functions of commit–reveal entropy, any client can re-derive a
/// child genome or replay a battle and confirm the server didn't cheat —
/// the off-chain preview of Arkade covenant enforcement.
/// </summary>
public static class FairnessAudit
{
    /// <summary>Rebuilds a battle-ready hero from its wire snapshot.</summary>
    public static Hero RebuildHero(HeroDto dto)
    {
        var hero = new Hero
        {
            Id = dto.Id,
            OwnerId = dto.OwnerId,
            Name = dto.Name,
            Genome = Genome.FromHex(dto.GenomeHex),
            Generation = dto.Generation,
            Level = dto.Level,
            Xp = dto.Xp,
        };
        foreach (var itemId in dto.Equipment.Values)
            if (ItemCatalog.Find(itemId) is { } item)
                hero.Equipment.Equip(item);
        return hero;
    }

    /// <summary>
    /// Verifies a breeding outcome: seed matches the commitment, entropy is the
    /// documented derivation, and the child genome equals
    /// <c>GeneMixer.Mix(parentA, parentB, entropy)</c>.
    /// </summary>
    public static (bool Ok, string Detail) VerifyBreeding(
        HeroDto parentA, HeroDto parentB, string nonce,
        string commitmentHex, BreedRevealResponse reveal)
    {
        var seed = Convert.FromHexString(reveal.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, parentA.Id, parentB.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(reveal.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, parentA, parentB, nonce)");

        var expected = GeneMixer.Mix(
            Genome.FromHex(parentA.GenomeHex), Genome.FromHex(parentB.GenomeHex), entropy);
        if (expected.ToHex() != reveal.Hero.GenomeHex)
            return (false, "child genome does not equal GeneMixer.Mix(parents, entropy)");

        var expectedGeneration = GeneMixer.ChildGeneration(parentA.Generation, parentB.Generation);
        if (expectedGeneration != reveal.Hero.Generation)
            return (false, $"child generation should be {expectedGeneration}");

        return (true, "seed, entropy, genome, and generation all verify");
    }

    /// <summary>
    /// Verifies a merge (fusion) outcome: seed matches the commitment, entropy is
    /// the documented derivation, and the fused genome equals
    /// <c>Fusion.Fuse(base, sacrifice, entropy)</c> — the deterministic recompute
    /// that makes a wrong mint detectable even before the covenant enforces it.
    /// </summary>
    public static (bool Ok, string Detail) VerifyMerge(
        string mergeId, HeroDto baseHero, HeroDto sacrificeHero, string nonce,
        string commitmentHex, MergeRevealResponse reveal)
    {
        var seed = Convert.FromHexString(reveal.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, mergeId, baseHero.Id, sacrificeHero.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(reveal.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, mergeId, base, sacrifice, nonce)");

        var expected = Fusion.Fuse(
            Genome.FromHex(baseHero.GenomeHex), Genome.FromHex(sacrificeHero.GenomeHex), entropy);
        if (expected.ToHex() != reveal.Hero.GenomeHex)
            return (false, "fused genome does not equal Fusion.Fuse(base, sacrifice, entropy)");

        var expectedGeneration = Math.Max(baseHero.Generation, sacrificeHero.Generation) + 1;
        if (expectedGeneration != reveal.Hero.Generation)
            return (false, $"fused generation should be {expectedGeneration}");

        return (true, "seed, entropy, fused genome, and generation all verify");
    }

    /// <summary>
    /// Verifies a death-match ABSORB outcome: seed matches the commitment, entropy is the documented
    /// fight derivation, and <c>Absorb.Resolve(winner, loser, entropy, odds)</c> reproduces the
    /// server's mint decision — on a mint, the new genome; on a keep, that nothing was minted. The
    /// odds are the server-published <see cref="AbsorbOdds"/> the client fetched. This is the
    /// mandatory client-side gate: a server can't hand the winner a genome the seed didn't produce
    /// (or fabricate an absorb) without this recompute catching it.
    /// </summary>
    public static (bool Ok, string Detail) VerifyAbsorb(
        string deathMatchId, HeroDto challenger, HeroDto defender, bool challengerWon,
        string nonce, string commitmentHex, AbsorbOdds odds,
        bool minted, string? newGenomeHex, string serverSeedHex, string entropyHex)
    {
        var seed = Convert.FromHexString(serverSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        // Same entropy derivation the fight used (VerifyMatch): (seed, matchId, challenger, defender, nonce).
        var entropy = CommitReveal.DeriveEntropy(seed, deathMatchId, challenger.Id, defender.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(entropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, deathMatchId, challenger, defender, nonce)");

        var winner = challengerWon ? challenger : defender;
        var loser = challengerWon ? defender : challenger;
        var outcome = Absorb.Resolve(
            Genome.FromHex(winner.GenomeHex), Genome.FromHex(loser.GenomeHex), entropy, odds);
        if (outcome.Minted != minted)
            return (false, $"minted flag mismatch: recomputed {outcome.Minted}, server reported {minted}");
        if (minted && !string.Equals(outcome.Result.ToHex(), newGenomeHex, StringComparison.OrdinalIgnoreCase))
            return (false, "absorbed genome does not equal Absorb.Resolve(winner, loser, entropy, odds)");

        return (true, minted ? "seed, entropy, and absorbed genome verify" : "seed, entropy, and keep (no absorb) verify");
    }

    /// <summary>
    /// Verifies a match outcome: seed matches the commitment, entropy is the
    /// documented derivation, and replaying <c>BattleEngine.Fight</c> over the
    /// pre-fight snapshots reproduces the exact event log.
    /// </summary>
    public static (bool Ok, string Detail) VerifyMatch(
        string matchId, string nonce, string commitmentHex, FightResponse fight)
    {
        var seed = Convert.FromHexString(fight.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(
            seed, matchId, fight.ChallengerSnapshot.Id, fight.DefenderSnapshot.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(fight.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, matchId, challenger, defender, nonce)");

        var replay = BattleEngine.Fight(
            RebuildHero(fight.ChallengerSnapshot), RebuildHero(fight.DefenderSnapshot), entropy);

        if (replay.WinnerId != fight.Result.WinnerId)
            return (false, "replayed winner differs from the reported winner");
        if (replay.Turns != fight.Result.Turns || replay.Events.Count != fight.Result.Events.Count)
            return (false, "replayed battle log differs in length");
        for (var i = 0; i < replay.Events.Count; i++)
        {
            var mine = replay.Events[i];
            var theirs = fight.Result.Events[i];
            if (mine.Turn != theirs.Turn || mine.ActorId != theirs.ActorId ||
                mine.Kind.ToString() != theirs.Kind || mine.SkillId != theirs.SkillId ||
                mine.Damage != theirs.Damage || mine.Crit != theirs.Crit ||
                mine.Healed != theirs.Healed || mine.TargetHpAfter != theirs.TargetHpAfter)
                return (false, $"replayed battle log diverges at event {i}");
        }

        return (true, $"battle replays identically over {replay.Events.Count} events");
    }

    /// <summary>
    /// Verifies a PvE gauntlet run (F1): seed matches the commitment, entropy is the documented
    /// derivation, and re-running <c>Gauntlet.Resolve</c> over the PRE-run hero snapshot reproduces the
    /// waves cleared — the ghosts and fights are pure in the entropy, so the server can't have picked
    /// soft foes. Then the capped XP and item are recomputed and checked against the SIGNED receipt, so a
    /// server can't over-award XP (past the level-10 cap) or fabricate a drop.
    /// </summary>
    public static (bool Ok, string Detail) VerifyGauntlet(
        string gauntletId, string nonce, string commitmentHex, GauntletRunResponse run)
    {
        var seed = Convert.FromHexString(run.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, gauntletId, run.HeroSnapshot.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(run.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, gauntletId, hero, nonce)");

        var resolved = Gauntlet.Resolve(RebuildHero(run.HeroSnapshot), entropy);
        if (resolved.WavesCleared != run.WavesCleared)
            return (false, $"replayed waves cleared ({resolved.WavesCleared}) differs from reported ({run.WavesCleared})");

        // Recompute the capped XP + item from the PRE-run level and check them against the signed receipt.
        var expectedXp = Gauntlet.XpForRun(run.HeroSnapshot.Level, resolved.WavesCleared);
        if (expectedXp != run.Receipt.XpAwardA)
            return (false, $"XP mismatch: recomputed {expectedXp}, receipt awarded {run.Receipt.XpAwardA}");
        var expectedItem = Gauntlet.RewardItem(entropy, resolved.WavesCleared);
        if (!string.Equals(expectedItem, run.ItemAwarded, StringComparison.Ordinal))
            return (false, $"item mismatch: recomputed {expectedItem ?? "none"}, server reported {run.ItemAwarded ?? "none"}");

        return (true, $"gauntlet verifies: {resolved.WavesCleared}/{Gauntlet.WaveCount} waves, {expectedXp} xp");
    }

    /// <summary>
    /// Verifies a 3v3 squad match: seed matches the commitment, entropy is the documented derivation, and
    /// re-running <c>SquadBattle.Resolve</c> over the pre-match lineup snapshots reproduces the best-of-3
    /// winner AND every duel's event log — so a server can't misreport a duel or the aggregate outcome.
    /// (Copies the VerifyGauntlet pattern; the engine + replay guarantee are untouched.)
    /// </summary>
    public static (bool Ok, string Detail) VerifySquad(
        string matchId, string nonce, string commitmentHex, SquadReplayDto replay)
    {
        var seed = Convert.FromHexString(replay.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, "squad", matchId, nonce);
        if (!Convert.ToHexString(entropy).Equals(replay.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, squad, matchId, nonce)");

        var challengers = replay.ChallengerLineup.Select(RebuildHero).ToList();
        var defenders = replay.DefenderLineup.Select(RebuildHero).ToList();
        var resolved = SquadBattle.Resolve(challengers, defenders, entropy);

        if (resolved.ChallengerWon != replay.Result.ChallengerWon)
            return (false, "replayed match winner differs from the reported winner");
        if (resolved.Duels.Count != replay.Result.Duels.Count)
            return (false, "replayed duel count differs");

        for (var d = 0; d < resolved.Duels.Count; d++)
        {
            var mine = resolved.Duels[d].Result;
            var theirs = replay.Result.Duels[d].Result;
            if (mine.WinnerId != theirs.WinnerId)
                return (false, $"replayed duel {d} winner differs from the reported winner");
            if (mine.Turns != theirs.Turns || mine.Events.Count != theirs.Events.Count)
                return (false, $"replayed duel {d} battle log differs in length");
            for (var i = 0; i < mine.Events.Count; i++)
            {
                var me = mine.Events[i];
                var th = theirs.Events[i];
                if (me.Turn != th.Turn || me.ActorId != th.ActorId || me.Kind.ToString() != th.Kind ||
                    me.SkillId != th.SkillId || me.Damage != th.Damage || me.Crit != th.Crit ||
                    me.Healed != th.Healed || me.TargetHpAfter != th.TargetHpAfter)
                    return (false, $"replayed duel {d} battle log diverges at event {i}");
            }
        }

        return (true, $"squad match verifies: {resolved.ChallengerWins}-{resolved.DefenderWins} over {resolved.Duels.Count} duels");
    }
}
