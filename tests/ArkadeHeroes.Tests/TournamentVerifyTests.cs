using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>Trustless replay of a tournament bracket — the highest-stakes flow (real-sats buy-ins -> pot ->
/// podium). A faithful TournamentReplayDto verifies; any tamper (champion, a fought-match winner, a
/// substituted entrant — id, genome, level or gear — the nonce, a broken commitment) is caught. Mirrors
/// SquadVerifyTests; closes the one resolvable outcome a client previously could not recompute.</summary>
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

    /// <summary>Every ordering of the list, depth-first — 720 for the 6-entrant field, cheap enough to sweep.</summary>
    static IEnumerable<List<Hero>> Permutations(List<Hero> heroes)
    {
        if (heroes.Count <= 1) { yield return heroes.ToList(); yield break; }
        for (var i = 0; i < heroes.Count; i++)
        {
            var rest = heroes.ToList();
            rest.RemoveAt(i);
            foreach (var tail in Permutations(rest)) { tail.Insert(0, heroes[i]); yield return tail; }
        }
    }

    /// <summary>The PRE-FIX seeding, frozen as the ATTACKER'S math: pair in the GIVEN list order with the
    /// positional per-fight sub-seed — exactly what Tournament.Resolve did when caller order WAS the bracket.
    /// A server owning the (uncommitted) entrant order could run this over every permutation and publish the
    /// one whose champion it liked; keeping a copy here pins that artifact as rejected even as the real
    /// resolver evolves.</summary>
    static (string ChampionId, List<TournamentMatchDto> Bracket) ResolveInCallerOrder(List<Hero> entrants, byte[] entropy)
    {
        var byId = entrants.ToDictionary(h => h.Id);
        var bracket = new List<TournamentMatchDto>();
        var alive = entrants.Select(h => h.Id).ToList();
        for (var round = 0; alive.Count > 1; round++)
        {
            var next = new List<string>();
            for (var i = 0; i < alive.Count; i += 2)
            {
                if (i + 1 >= alive.Count) { next.Add(alive[i]); continue; }   // bye — never on the wire
                var fightSeed = CommitReveal.DeriveEntropy(entropy, "tourney-fight", $"{round}-{i / 2}");
                var result = BattleEngine.Fight(byId[alive[i]], byId[alive[i + 1]], fightSeed);
                bracket.Add(new TournamentMatchDto(round, i / 2, alive[i], alive[i + 1], result.WinnerId));
                next.Add(result.WinnerId);
            }
            alive = next;
        }
        return (alive[0], bracket);
    }

    [Fact]
    public void Resolve_ChampionIsIndependentOfEntrantOrder_SoTheServerCannotSeedTheBracket()
    {
        // The bracket seeding must derive from the COMMITTED seed, not caller order — else a server reorders
        // honest entrants to crown any champion of the real-sats pot, and VerifyTournament (which re-runs
        // Resolve over the server's order) waves it through.
        var seed = new byte[32];
        Array.Fill(seed, (byte)5);
        var entropy = CommitReveal.DeriveEntropy(seed, "tournament", "t1", "nonce");
        var entrants = Entrants();
        var honest = Tournament.Resolve(entrants, entropy).ChampionId;

        // EVERY permutation must resolve to the SAME champion — pre-fix, some ordering crowned another,
        // which is exactly the exploit. 720 tiny brackets keep this exhaustive, not sampled.
        foreach (var perm in Permutations(entrants))
            Assert.Equal(honest, Tournament.Resolve(perm, entropy).ChampionId);
    }

    [Fact]
    public void VerifyTournament_RejectsTheEntrantReorderAttack()
    {
        const string id = "t3";
        const string nonce = "n3";
        var seed = new byte[32];
        Array.Fill(seed, (byte)7);
        var entrants = Entrants();
        var honest = BuildReplay(id, nonce, seed, entrants, out var commitment);
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(honest.Entrants);

        // The attack: the entrant ORDER is not committed, so a server resolves every permutation under the
        // pre-fix caller-order seeding and publishes the one whose champion it prefers — honest genomes,
        // honest seed, honest entropy, self-consistent bracket. Find one that crowns a different champion.
        var entropy = CommitReveal.DeriveEntropy(seed, "tournament", id, nonce);
        var attackRun = Permutations(entrants)
            .Select(perm => (Perm: perm, Run: ResolveInCallerOrder(perm, entropy)))
            .FirstOrDefault(x => x.Run.ChampionId != honest.ChampionHeroId);
        Assert.NotNull(attackRun.Perm);   // the lever is real: some ordering crowns a different champion

        // Pre-fix, VerifyTournament replayed this artifact verbatim and returned Ok. Now the resolver
        // re-seeds from the committed seed, so the rigged order replays to the honest champion and the
        // reported one mismatches — the pot can no longer be steered by reordering.
        var attack = honest with
        {
            Entrants = attackRun.Perm.Select(h => h.ToDto()).ToList(),
            Bracket = attackRun.Run.Bracket,
            ChampionHeroId = attackRun.Run.ChampionId,
        };
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, attack).Ok);

        // Flip side: order is now INERT — the same reordered entrants under the HONEST bracket + champion
        // still verify (the entrant-set commitment sorts by id, so a reorder doesn't change it either),
        // meaning a benign transport/storage reorder can't brick a genuine replay.
        var reorderedHonest = honest with { Entrants = attackRun.Perm.Select(h => h.ToDto()).ToList() };
        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, reorderedHonest).Ok);
    }

    [Fact]
    public void VerifyTournament_AcceptsFaithful_RejectsTampered()
    {
        const string id = "t1";
        const string nonce = "nonce";
        var seed = new byte[32];
        Array.Fill(seed, (byte)5);
        var replay = BuildReplay(id, nonce, seed, Entrants(), out var commitment);
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(replay.Entrants);

        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, replay).Ok);

        // Tamper 1: claim a different champion took the pot.
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment,
            replay with { ChampionHeroId = "phantom" }).Ok);

        // Tamper 2: flip a fought match's winner.
        var badBracket = replay.Bracket.ToList();
        badBracket[0] = badBracket[0] with { WinnerId = "phantom" };
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment,
            replay with { Bracket = badBracket }).Ok);

        // Tamper 3: substitute a ringer for an entrant — a changed SET breaks the entrant-set commitment
        // first (and even without it, slot 0 always fights round 0, so the ringer's id couldn't match the
        // reported bracket's).
        var badEntrants = replay.Entrants.ToList();
        badEntrants[0] = MakeHero("ringer", 50, 250).ToDto();
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment,
            replay with { Entrants = badEntrants }).Ok);

        // Tamper 4: verify under a different nonce than the bracket was drawn with.
        Assert.False(FairnessAudit.VerifyTournament(id, "wrong-nonce", commitment, entrantsCommitment, replay).Ok);
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
        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitToOtherSeed,
            FairnessAudit.ComputeEntrantsCommitment(replay.Entrants), replay).Ok);
    }

    // ── The entrant-SUBSTITUTION attack: #102 pinned the bracket ORDER to the seed, but the entrant
    // SNAPSHOTS were still the server's word — swap one entrant's genome/level/gear in the replay and
    // re-run the resolver over the forged field, and every seed/entropy/champion/bracket re-check passes
    // (the forgery is self-consistent). Only a commitment to the entrant SET, taken when the bracket
    // fills and fetched independently of the replay, can catch it. ──

    /// <summary>Entrants carrying GEAR in the honest field, so the commitment provably binds equipment too.</summary>
    static List<Hero> GearedEntrants()
    {
        var entrants = Entrants();
        entrants[0].Equipment.Equip(ItemCatalog.Find("rusty-blade")!);
        entrants[3].Equipment.Equip(ItemCatalog.Find("lucky-feather")!);
        return entrants;
    }

    /// <summary>The substitution: swap ONE entrant's wire snapshot, then let the REAL resolver recompute
    /// the bracket + champion over the forged field — a fully self-consistent replay (honest seed, honest
    /// entropy, a bracket that replays exactly) that only the entrant-set commitment can catch.</summary>
    static TournamentReplayDto SubstituteEntrant(
        string id, string nonce, byte[] seed, List<Hero> entrants, int victim, Func<HeroDto, HeroDto> tamper)
    {
        var forged = entrants.Select(h => h.ToDto()).ToList();
        forged[victim] = tamper(forged[victim]);
        return BuildReplay(id, nonce, seed, forged.Select(FairnessAudit.RebuildHero).ToList(), out _);
    }

    [Fact]
    public void VerifyTournament_RejectsAnEntrantGenomeSubstitution()
    {
        const string id = "t5";
        const string nonce = "n5";
        var seed = new byte[32];
        Array.Fill(seed, (byte)11);
        var entrants = GearedEntrants();
        var honest = BuildReplay(id, nonce, seed, entrants, out var commitment);
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(honest.Entrants);
        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, honest).Ok);

        // Weaken entrant 3's genome to a genome the fill-time field never held — "soften my opponent".
        var weak = new byte[32];
        Array.Fill(weak, (byte)199);
        var forged = SubstituteEntrant(id, nonce, seed, entrants, 3,
            d => d with { GenomeHex = Genome.NewGen0(weak).ToHex() });
        var verdict = FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, forged);
        Assert.False(verdict.Ok);
        Assert.Contains("entrant-set commitment", verdict.Detail);
    }

    [Fact]
    public void VerifyTournament_RejectsAnEntrantLevelSubstitution()
    {
        const string id = "t6";
        const string nonce = "n6";
        var seed = new byte[32];
        Array.Fill(seed, (byte)13);
        var entrants = GearedEntrants();
        var honest = BuildReplay(id, nonce, seed, entrants, out var commitment);
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(honest.Entrants);
        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, honest).Ok);

        // Drop entrant 1 from its true level 5 to 1 — weaker stats AND a stripped skill kit.
        var forged = SubstituteEntrant(id, nonce, seed, entrants, 1, d => d with { Level = 1 });
        var verdict = FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, forged);
        Assert.False(verdict.Ok);
        Assert.Contains("entrant-set commitment", verdict.Detail);
    }

    [Fact]
    public void VerifyTournament_RejectsAnEntrantGearSubstitution()
    {
        const string id = "t7";
        const string nonce = "n7";
        var seed = new byte[32];
        Array.Fill(seed, (byte)17);
        var entrants = GearedEntrants();
        var honest = BuildReplay(id, nonce, seed, entrants, out var commitment);
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(honest.Entrants);
        Assert.True(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, honest).Ok);

        // Swap entrant 0's committed rusty-blade for the top-tier weapon it never equipped.
        var forged = SubstituteEntrant(id, nonce, seed, entrants, 0,
            d => d with { Equipment = new Dictionary<string, string> { ["Weapon"] = "arkforged-edge" } });
        var verdict = FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, forged);
        Assert.False(verdict.Ok);
        Assert.Contains("entrant-set commitment", verdict.Detail);
    }
}
