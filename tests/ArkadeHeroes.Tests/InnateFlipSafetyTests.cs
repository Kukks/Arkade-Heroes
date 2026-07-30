using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;   // GameOptions + DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The evidence that turning <c>GameOptions.InnateAbilities</c> ON is safe for VERIFICATION — which a green
/// unit suite on its own CANNOT show, and which is the entire risk of this flip.
///
/// The rest of the suite fights gen-0 starters, and <c>Genome.NewGen0</c> clears <c>genes[16..]</c>, so a
/// starter expresses NO cosmetic trait, grants no passive, and never rolls one: an ON-config fight between
/// starters is event-identical to a Default one. A previous attempt at this flip therefore read fully GREEN
/// while being catastrophically unsafe. With BRED heroes the same flip rejected roughly a quarter of gen-1
/// matchups — rejected if and only if a hero expressed a passive, deterministically, not probabilistically.
///
/// So every fighter here is a real <c>GeneMixer.Mix</c> child of trait-carrying parents, asserted at runtime
/// to express passives, fought under the config the server now runs, and verified through the exact path the
/// SDK uses: serve <see cref="GameRulesDto"/> for the stamp, rebuild, re-hash, replay. Each test also counts
/// how many of those same outcomes a STAMP-IGNORING client would reject — the pre-#148 behaviour, and the
/// number that makes the blindness of a starters-only gate legible. All five resolvable outcome types are
/// covered, because a fix that left Gauntlet/Trials/Squad/Tournament unverifiable is a shipped permakill bug.
/// </summary>
public class InnateFlipSafetyTests
{
    /// <summary>The rules the SERVER now resolves under: GameOptions defaults, i.e. the flip LIVE.</summary>
    private static GameConfig Live { get; } = new GameOptions().ToGameConfig();

    /// <summary>The stamp those rules hash to — provably not the compiled-in default one.</summary>
    private static string Stamp { get; } = GameConfigVersion.Compute(Live);

    /// <summary>The config a client gets by RESOLVING the stamp — rebuilt from the served
    /// <see cref="GameRulesDto"/> exactly as <c>ConfigApi.ResolveAsync</c> does, not by reaching for
    /// <see cref="Live"/> directly. Verifying against the resolved object is what makes these tests evidence
    /// about the shipped path rather than about a local constant.</summary>
    private static GameConfig Resolved { get; } = GameRulesDto.From(Live).ToGameConfig()!;

    /// <summary>A trait-carrying breeding parent derived from a seed: usable stat genes, and all six cosmetic
    /// categories drawn across the Uncommon..Legendary band, so children express a VARIED set of passives at
    /// varied tiers rather than one uniform maximal genome.</summary>
    private static Genome Parent(string seed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var b = new byte[Genome.Size];
        for (var i = 0; i < 16; i++) b[i] = (byte)(120 + h[i] % 120);
        for (var cat = 0; cat < 6; cat++) b[16 + cat * 2] = (byte)(207 + h[16 + cat] % 49);
        return new Genome(b);
    }

    /// <summary>A genuinely BRED hero: <c>GeneMixer.Mix</c> over two trait-carrying parents, generation 1.
    /// Asserts the child expresses at least one innate passive, so this population can never silently decay
    /// into the gen-0 case where the on/off configs fight identically and the test proves nothing.</summary>
    private static Hero Bred(int i, int level)
    {
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData("innate-flip"u8.ToArray()), $"h{i}", "bred");
        var genome = GeneMixer.Mix(Parent($"pa{i}"), Parent($"pb{i}"), entropy);
        Assert.True(Traits.InnatePassives(genome, Live).Count > 0,
            $"fixture hero h{i} expresses no innate passive — the flip would be inert for it");
        return new Hero
        {
            Id = $"h{i}",
            OwnerId = "p",
            Name = $"h{i}",
            Genome = genome,
            Generation = GeneMixer.ChildGeneration(0, 0),
            Level = level,
        };
    }

    private static List<Hero> Population(int count, int baseLevel = 10) =>
        Enumerable.Range(0, count).Select(i => Bred(i, baseLevel + i % 20)).ToList();

    private static readonly BattleEventKind[] PassiveBeats =
    [
        BattleEventKind.ShieldAbsorbed, BattleEventKind.Regenerated,
        BattleEventKind.Thorns, BattleEventKind.Burned,
    ];

    private static bool HasPassiveBeat(IEnumerable<BattleEvent> events) =>
        events.Any(e => PassiveBeats.Contains(e.Kind));

    [Fact]
    public void TheFlipIsLiveInConfig_ButCombatConfigDefaultStaysOff()
    {
        // The switch is configuration-driven and defaults ON, so an operator can turn it back off without a
        // code change...
        Assert.True(new GameOptions().InnateAbilities);
        Assert.True(Live.Combat.InnateAbilities);
        Assert.False(new GameOptions { InnateAbilities = false }.ToGameConfig().Combat.InnateAbilities);

        // ...while CombatConfig.Default STAYS off. That constant is what every UNSTAMPED replay is
        // reconstructed under, so flipping it there would silently rewrite what history is checked against.
        Assert.False(CombatConfig.Default.InnateAbilities);
        Assert.False(GameConfig.Default.Combat.InnateAbilities);

        // Turning it on genuinely moves the version id, which is what makes the flip visible to a verifier
        // instead of silent.
        Assert.NotEqual(GameConfigVersion.Default, Stamp);

        // The live stamp is EXACTLY the two flags the server ships, enumerated: innate (#149) and gear
        // counters. GearCounters joined the live config when the hero card began showing build shape, so the
        // delta from Default is no longer innate alone — this line was updated deliberately for that, and it
        // stays an exact equality on purpose. It is the RATCHET that catches a THIRD rules flag going live
        // without anyone re-deriving the stamp, so it must keep enumerating rather than relax to NotEqual.
        Assert.Equal(Stamp, GameConfigVersion.Compute(GameConfig.Default with
        {
            Combat = GameConfig.Default.Combat with { InnateAbilities = true, GearCounters = true },
        }));

        // And the stamp round-trips through the wire the client actually resolves over.
        Assert.Equal(Stamp, GameConfigVersion.Compute(Resolved));
        Assert.True(Resolved.Combat.InnateAbilities);
    }

    // ── 1/5: duel (VerifyMatch) — the population test ──────────────────────────────────────────────

    [Fact]
    public void Duels_BetweenBredHeroes_AllVerifyUnderTheStamp_WhileAStampBlindClientRejectsMany()
    {
        const int matchups = 400;
        var heroes = Population(64);
        var expressed = heroes.Count(h => Traits.InnatePassives(h.Genome, Live).Count > 0);

        int verified = 0, rejected = 0, blindRejected = 0, withBeat = 0;
        for (var m = 0; m < matchups; m++)
        {
            var a = heroes[m % heroes.Count];
            var b = heroes[(m * 7 + 13) % heroes.Count];
            if (a.Id == b.Id) b = heroes[(m * 7 + 14) % heroes.Count];

            var matchId = $"flip-duel-{m}";
            var nonce = $"flip-duel-nonce-{m}";
            var seed = SHA256.HashData(BitConverter.GetBytes(m));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, matchId, a.Id, b.Id, nonce);

            var result = BattleEngine.Fight(a, b, entropy, Live);
            if (HasPassiveBeat(result.Events)) withBeat++;

            var da = a.ToDto();
            var db = b.ToDto();
            var fight = new FightResponse(result.ToDto(), Convert.ToHexString(seed), Convert.ToHexString(entropy),
                0, 0, da, db, da, db, 0, 0, null, Stamp);

            if (FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, Resolved).Ok) verified++;
            else rejected++;
            if (!FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight).Ok) blindRejected++;
        }

        Assert.Equal(64, expressed);            // every fighter actually carries passives
        Assert.Equal(matchups, verified);       // ...and every outcome verifies under its stamp
        Assert.Equal(0, rejected);

        // Not an inert sweep: measured 196/400 logs contain a passive BEAT (a shield, mend, thorns or
        // brand tick actually landing). A bound rather than the exact figure, because retuning the
        // InnateBonuses proc chances legitimately moves it and should not fail this test cryptically.
        Assert.True(withBeat >= 150, $"only {withBeat}/{matchups} logs contained a passive beat");

        // And the number that makes the blindness concrete: a client replaying under its own compiled-in
        // GameConfig.Default rejects EVERY ONE of these honest outcomes. Deterministic, not probabilistic
        // — a divergence lands whenever a fighter expresses a passive, and here all 64 do.
        Assert.Equal(matchups, blindRejected);
    }

    // ── 2/5: gauntlet (VerifyGauntlet) ─────────────────────────────────────────────────────────────

    [Fact]
    public void GauntletRuns_ByBredHeroes_AllVerifyUnderTheStamp()
    {
        const int runs = 60;
        var heroes = Population(runs, baseLevel: 6);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var i = 0; i < runs; i++)
        {
            var hero = heroes[i];
            var gauntletId = $"flip-gauntlet-{i}";
            var nonce = $"flip-gauntlet-nonce-{i}";
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(gauntletId));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, gauntletId, hero.Id, nonce);

            var run = Gauntlet.Resolve(hero, entropy, Live);
            var snapshot = hero.ToDto();
            var xp = Gauntlet.XpForRun(snapshot.Level, run.WavesCleared);
            var receipt = new ProgressionReceiptDto(
                "gauntlet", gauntletId, hero.Id, "", hero.Id,
                Convert.ToHexString(seed), nonce, commitment, xp, 0, hero.Level, hero.Level, 0, "", "");
            var response = new GauntletRunResponse(
                run.WavesCleared, [], xp, hero.Level, Gauntlet.RewardItem(entropy, run.WavesCleared), null,
                snapshot, Convert.ToHexString(seed), Convert.ToHexString(entropy), receipt, Stamp);

            if (FairnessAudit.VerifyGauntlet(gauntletId, nonce, commitment, response, Resolved).Ok) verified++;
            else rejected++;
            if (!FairnessAudit.VerifyGauntlet(gauntletId, nonce, commitment, response).Ok) blindRejected++;
        }

        Assert.Equal(runs, verified);
        Assert.Equal(0, rejected);

        // Measured 20/60. Lower than the duel sweep's 100% because VerifyGauntlet checks the AGGREGATE
        // (waves cleared + capped XP + item), not every event — a passive-shifted log that clears the same
        // number of waves still reconciles. So this number understates the divergence rather than bounding it.
        Assert.True(blindRejected >= 10, $"only {blindRejected}/{runs} runs diverged for a stamp-blind client");
    }

    // ── 3/5: trials (VerifyTrials) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TrialsRuns_ByBredHeroes_AllVerifyUnderTheStamp()
    {
        const int runs = 40;
        const TrialsAffix affix = TrialsAffix.None;
        var heroes = Population(runs, baseLevel: 10);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var i = 0; i < runs; i++)
        {
            var hero = heroes[i];
            var trialsId = $"flip-trials-{i}";
            var nonce = $"flip-trials-nonce-{i}";
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(trialsId));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, trialsId, hero.Id, nonce);

            var run = Trials.Resolve(hero, entropy, Live, affix);
            var receipt = new ProgressionReceiptDto(
                "trials", trialsId, hero.Id, "", hero.Id,
                Convert.ToHexString(seed), nonce, commitment, 0, run.WavesCleared,
                hero.Level, hero.Level, 0, "", "");
            var response = new TrialsRunResponse(
                run.WavesCleared, [], Trials.TitleFor(run.WavesCleared), run.WavesCleared, affix.ToString(),
                hero.ToDto(), Convert.ToHexString(seed), Convert.ToHexString(entropy), receipt, Stamp);

            if (FairnessAudit.VerifyTrials(trialsId, nonce, commitment, response, Resolved).Ok) verified++;
            else rejected++;
            if (!FairnessAudit.VerifyTrials(trialsId, nonce, commitment, response).Ok) blindRejected++;
        }

        Assert.Equal(runs, verified);
        Assert.Equal(0, rejected);

        // Measured 29/40 (VerifyTrials checks waves survived + title + score, not every event).
        Assert.True(blindRejected >= 15, $"only {blindRejected}/{runs} runs diverged for a stamp-blind client");
    }

    // ── 4/5: squad (VerifySquad) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SquadMatches_BetweenBredLineups_AllVerifyUnderTheStamp()
    {
        const int matches = 40;
        var heroes = Population(48, baseLevel: 12);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var m = 0; m < matches; m++)
        {
            // Disjoint halves: SquadBattle pairs slot against slot, and a hero cannot fight itself.
            var challengers = Enumerable.Range(0, 3).Select(k => heroes[(m * 3 + k) % 24]).ToList();
            var defenders = Enumerable.Range(0, 3).Select(k => heroes[24 + (m * 5 + k) % 24]).ToList();

            var matchId = $"flip-squad-{m}";
            var nonce = $"flip-squad-nonce-{m}";
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(matchId));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, "squad", matchId, nonce);

            var result = SquadBattle.Resolve(challengers, defenders, entropy, Live);
            var resultDto = new SquadResultDto(
                result.ChallengerWon, result.ChallengerWins, result.DefenderWins,
                result.Duels.Select(x => new SquadDuelDto(
                    x.Slot, challengers[x.Slot].ToDto(), defenders[x.Slot].ToDto(), x.Result.ToDto())).ToList());
            var replay = new SquadReplayDto(
                challengers.Select(h => h.ToDto()).ToList(), defenders.Select(h => h.ToDto()).ToList(),
                resultDto, commitment, Convert.ToHexString(seed), Convert.ToHexString(entropy), nonce, Stamp);

            if (FairnessAudit.VerifySquad(matchId, nonce, commitment, replay, Resolved).Ok) verified++;
            else rejected++;
            if (!FairnessAudit.VerifySquad(matchId, nonce, commitment, replay).Ok) blindRejected++;
        }

        Assert.Equal(matches, verified);
        Assert.Equal(0, rejected);

        // Squad checks every duel's full event log, so — like the duel sweep — the flip is 100% detectable.
        Assert.Equal(matches, blindRejected);
    }

    // ── 5/5: tournament (VerifyTournament) ─────────────────────────────────────────────────────────

    [Fact]
    public void TournamentBrackets_OfBredEntrants_AllVerifyUnderTheStamp()
    {
        const int brackets = 20;
        var heroes = Population(48, baseLevel: 12);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var t = 0; t < brackets; t++)
        {
            var entrants = Enumerable.Range(0, 6).Select(k => heroes[(t * 6 + k) % heroes.Count]).ToList();

            var id = $"flip-tourney-{t}";
            var nonce = $"flip-tourney-nonce-{t}";
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, "tournament", id, nonce);

            var result = Tournament.Resolve(entrants, entropy, Live);
            var entrantDtos = entrants.Select(h => h.ToDto()).ToList();
            var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(entrantDtos);
            var replay = new TournamentReplayDto(
                entrantDtos,
                result.Matches.Where(x => x.Result is not null)
                    .Select(x => new TournamentMatchDto(x.Round, x.Index, x.AId, x.BId, x.WinnerId)).ToList(),
                result.ChampionId, commitment, Convert.ToHexString(seed), Convert.ToHexString(entropy), nonce,
                entrantsCommitment, Stamp);

            if (FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, replay, Resolved).Ok)
                verified++;
            else rejected++;
            if (!FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, replay).Ok)
                blindRejected++;
        }

        Assert.Equal(brackets, verified);
        Assert.Equal(0, rejected);

        // Measured 15/20 (VerifyTournament checks the champion + the fought bracket, not every event).
        Assert.True(blindRejected >= 8, $"only {blindRejected}/{brackets} brackets diverged for a stamp-blind client");
    }

    // ── the historical bar the flip must not move ──────────────────────────────────────────────────

    [Fact]
    public void UnstampedHistory_StillReplaysWithPassivesOFF_DespiteTheServerNowRunningThemOn()
    {
        // The property the flip most easily breaks: an outcome resolved BEFORE the flip carries no stamp (or
        // the default one) and ran with passives off. It must keep reconstructing that way no matter what the
        // running server does now — which is exactly why CombatConfig.Default was not edited.
        const string matchId = "pre-flip-duel";
        const string nonce = "pre-flip-nonce";
        var a = Bred(900, 18);
        var b = Bred(901, 18);

        var seed = SHA256.HashData("pre-flip"u8.ToArray());
        var commitment = CommitReveal.Commit(seed);
        var entropy = CommitReveal.DeriveEntropy(seed, matchId, a.Id, b.Id, nonce);
        var result = BattleEngine.Fight(a, b, entropy);   // no config → Default, as history ran
        var da = a.ToDto();
        var db = b.ToDto();
        var fight = new FightResponse(result.ToDto(), Convert.ToHexString(seed),
            Convert.ToHexString(entropy), 0, 0, da, db, da, db);

        Assert.Equal("", fight.ConfigVersion);   // trailing optional → an old server's JSON lands here
        var (ok, detail) = FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight);
        Assert.True(ok, detail);

        // And these are trait-expressing heroes, so the two configs genuinely disagree for them — the
        // preservation above is a real property, not a tautology about inert fixtures.
        Assert.NotEmpty(Traits.InnatePassives(a.Genome, Live));
        Assert.False(FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, Resolved).Ok);
    }
}
