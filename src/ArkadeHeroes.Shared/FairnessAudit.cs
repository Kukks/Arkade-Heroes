using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

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
}
