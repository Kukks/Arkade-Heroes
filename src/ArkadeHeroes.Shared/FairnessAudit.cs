using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    /// Verifies an endless Trials run: seed matches the commitment, entropy is the documented derivation,
    /// and re-running <c>Trials.Resolve</c> over the PRE-run hero snapshot reproduces the waves survived —
    /// the ghost ladder and fights are pure in the entropy, so the server can't have picked soft foes. The
    /// title is recomputed from the score, and the score is checked against the SIGNED receipt (it rides in
    /// XpAwardB), so a server can't fabricate a leaderboard result.
    /// </summary>
    public static (bool Ok, string Detail) VerifyTrials(
        string trialsId, string nonce, string commitmentHex, TrialsRunResponse run)
    {
        var seed = Convert.FromHexString(run.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, trialsId, run.HeroSnapshot.Id, nonce);
        if (!Convert.ToHexString(entropy).Equals(run.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, trialsId, hero, nonce)");

        // Replay under the run's PINNED weekly affix, not whatever affix is in force now — otherwise a run
        // resolved near a week boundary would fail its own verification minutes later.
        if (!Enum.TryParse<TrialsAffix>(run.Affix, out var affix))
            return (false, $"unknown weekly affix '{run.Affix}' — cannot replay the ladder faithfully");

        var resolved = Trials.Resolve(RebuildHero(run.HeroSnapshot), entropy, affix: affix);
        if (resolved.WavesCleared != run.WavesCleared)
            return (false, $"replayed waves survived ({resolved.WavesCleared}) differs from reported ({run.WavesCleared})");

        var expectedTitle = Trials.TitleFor(resolved.WavesCleared);
        if (!string.Equals(expectedTitle, run.Title, StringComparison.Ordinal))
            return (false, $"title mismatch: recomputed {expectedTitle ?? "none"}, server reported {run.Title ?? "none"}");
        if (resolved.WavesCleared != run.Receipt.XpAwardB)
            return (false, $"score mismatch: replayed {resolved.WavesCleared}, receipt attests {run.Receipt.XpAwardB}");

        return (true, $"trials verifies: {resolved.WavesCleared} waves survived{(expectedTitle is null ? "" : $", {expectedTitle}")}");
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

    /// <summary>The domain tag pinning the entrant-set commitment's canonical serialization version.</summary>
    private const string EntrantsCommitmentTag = "arkade-tournament-entrants-v1";

    /// <summary>
    /// The tournament entrant-set commitment: a domain-tagged SHA-256 over the CANONICAL serialization of
    /// the entrant snapshots — per entrant its id, genome hex, level, and equipped item ids, the exact
    /// inputs a fight consumes (<c>StatBlock.ComputeFor</c> + <c>SkillCatalog.SkillsFor</c> +
    /// <see cref="RebuildHero"/>) and nothing else. Canonical means deterministic on BOTH sides of the
    /// wire: entrants sort by id and item ids sort Ordinal (culture-independent, like the resolver's
    /// seeding), the genome hex is lower-cased invariantly, the level renders InvariantCulture, and the
    /// hash reads UTF-8 bytes — so server, x64 client, and WASM client compute byte-identical commitments
    /// in any locale. The separators are unambiguous because every field's alphabet excludes them (ids are
    /// server-minted hex tags, genomes are hex, levels are digits, item ids are catalog constants). The
    /// server computes + publishes this on the tournament DTO the moment the bracket FILLS;
    /// <see cref="VerifyTournament"/> recomputes it over the replay's snapshots — closing the one gap
    /// #102's seed-drawn seeding left open: a server substituting an entrant's genome/level/gear.
    /// </summary>
    public static string ComputeEntrantsCommitment(IEnumerable<HeroDto> entrants)
    {
        var canon = new StringBuilder(EntrantsCommitmentTag);
        foreach (var e in entrants.OrderBy(h => h.Id, StringComparer.Ordinal))
        {
            canon.Append('\n').Append(e.Id)
                 .Append('|').Append(e.GenomeHex.ToLowerInvariant())
                 .Append('|').Append(e.Level.ToString(CultureInfo.InvariantCulture));
            foreach (var itemId in e.Equipment.Values.OrderBy(i => i, StringComparer.Ordinal))
                canon.Append('|').Append(itemId);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a tournament bracket: the replay's entrant snapshots match the FILL-time entrant-set
    /// commitment, the revealed seed matches its commitment, the entropy is the documented derivation, and
    /// re-running <c>Tournament.Resolve</c> over the entrant snapshots reproduces the champion AND every
    /// fought bracket match — so the server can't misreport who took the real-sats pot. The bracket
    /// SEEDING is drawn from the seed (caller order is inert), so a reordered entrant list can't change the
    /// outcome; the entrant-set commitment (fetched from the tournament DTO, NOT from this replay) pins the
    /// snapshots themselves, so a substituted genome/level/gear can't either. Mirrors
    /// <see cref="VerifySquad"/>; the resolver + replay guarantee are untouched.
    /// </summary>
    public static (bool Ok, string Detail) VerifyTournament(
        string tournamentId, string nonce, string commitmentHex, string entrantsCommitmentHex,
        TournamentReplayDto replay)
    {
        // Pin the entrant SET before anything else: a substituted snapshot re-resolves self-consistently
        // (same seed, same entropy, a bracket that replays), so no downstream check would catch it — only
        // this recompute against the commitment the server published when the bracket filled can.
        if (!ComputeEntrantsCommitment(replay.Entrants)
                .Equals(entrantsCommitmentHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entrant snapshots do not match the fill-time entrant-set commitment");

        var seed = Convert.FromHexString(replay.ServerSeedHex);
        if (!CommitReveal.Verify(seed, commitmentHex))
            return (false, "revealed server seed does not match the commitment");

        var entropy = CommitReveal.DeriveEntropy(seed, "tournament", tournamentId, nonce);
        if (!Convert.ToHexString(entropy).Equals(replay.EntropyHex, StringComparison.OrdinalIgnoreCase))
            return (false, "entropy does not match DeriveEntropy(seed, tournament, tournamentId, nonce)");

        var entrants = replay.Entrants.Select(RebuildHero).ToList();
        var resolved = Tournament.Resolve(entrants, entropy);

        if (resolved.ChampionId != replay.ChampionHeroId)
            return (false, "replayed champion differs from the reported champion");

        // The wire bracket carries only FOUGHT matches (a bye has no BattleResult), so replay the same
        // projection the server sends — the byes are implied by the resolver over the same entrants + entropy.
        var mine = resolved.Matches.Where(m => m.Result is not null).ToList();
        if (mine.Count != replay.Bracket.Count)
            return (false, $"replayed bracket has {mine.Count} fought matches, reported {replay.Bracket.Count}");
        for (var i = 0; i < mine.Count; i++)
        {
            var m = mine[i];
            var r = replay.Bracket[i];
            if (m.Round != r.Round || m.Index != r.Index || m.AId != r.AId || m.BId != r.BId || m.WinnerId != r.WinnerId)
                return (false, $"replayed bracket match {i} differs from the reported bracket");
        }

        return (true, $"tournament verifies: {mine.Count} fought matches, champion {resolved.ChampionId}");
    }
}
