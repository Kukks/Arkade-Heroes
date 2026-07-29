using System.Security.Cryptography;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The keystone: an outcome resolved under NON-DEFAULT rules verifies only when the verifier replays under
/// the config the replay's stamp names — and fails honestly when the stamp is absent (the old compile-time
/// GameConfig.Default path) or unresolvable. Covers all five resolvable outcomes: duel, gauntlet, trials,
/// squad, tournament.
///
/// EVERY fighter here is BRED — a real GeneMixer.Mix child of trait-carrying parents — and every test asserts
/// at runtime that its heroes actually EXPRESS passives and that the resolved log actually CONTAINS passive
/// beats. That is deliberate: gen-0 starters come out of Genome.NewGen0 with genes[16..] cleared, so they
/// express nothing, nothing procs, and an on-config fight is byte-identical to a Default one. A test built on
/// starters would pass whether or not any of this worked.
/// </summary>
public class ConfigStampReplayTests
{
    /// <summary>The non-default rules under test: innate-v2 passives on, which only bred heroes can express.</summary>
    private static GameConfig Innate { get; } =
        GameConfig.Default with { Combat = GameConfig.Default.Combat with { InnateAbilities = true } };

    /// <summary>A trait-carrying breeding parent: usable stat genes, and all six cosmetic categories
    /// expressed Legendary so every child inherits a genome that actually grants passives.</summary>
    private static Genome Parent(byte statGenes)
    {
        var b = new byte[Genome.Size];
        for (var i = 0; i < 16; i++) b[i] = statGenes;                 // stat + skill genes
        for (var cat = 0; cat < 6; cat++) b[16 + cat * 2] = 255;       // dominant Aura..Stance, Legendary
        return new Genome(b);
    }

    /// <summary>
    /// A genuinely BRED hero: GeneMixer.Mix over two trait-carrying parents, generation 1. Asserts the child
    /// expresses at least one innate passive, so this fixture can never silently decay into the gen-0 case
    /// where the on/off configs fight identically and the test proves nothing.
    /// </summary>
    private static Hero Bred(string id, int level, byte statsA, byte statsB, string breedNonce)
    {
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData("bred"u8.ToArray()), id, breedNonce);
        var genome = GeneMixer.Mix(Parent(statsA), Parent(statsB), entropy);

        var passives = Traits.InnatePassives(genome, Innate);
        Assert.True(passives.Count > 0,
            $"fixture hero {id} expresses no innate passives — the config difference would be inert");

        return new Hero
        {
            Id = id,
            OwnerId = "p",
            Name = id,
            Genome = genome,
            Generation = GeneMixer.ChildGeneration(0, 0),
            Level = level,
        };
    }

    private static readonly BattleEventKind[] PassiveBeats =
    [
        BattleEventKind.ShieldAbsorbed, BattleEventKind.Regenerated,
        BattleEventKind.Thorns, BattleEventKind.Burned,
    ];

    private static bool HasPassiveBeat(IEnumerable<BattleEvent> events) =>
        events.Any(e => PassiveBeats.Contains(e.Kind));

    /// <summary>A deterministic seed sweep for one whose ON-config outcome actually exercises the passives —
    /// asserted by the caller, so a fixture that stopped diverging fails the test instead of quietly passing.</summary>
    private static byte[]? FindSeed(Func<byte[], bool> diverges, int tries = 400)
    {
        for (var i = 0; i < tries; i++)
        {
            var seed = SHA256.HashData(BitConverter.GetBytes(i));
            if (diverges(seed)) return seed;
        }
        return null;
    }

    // ── 1/5: duel (VerifyMatch) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Duel_VerifiesUnderTheStampedConfig_AndFailsHonestlyWithout()
    {
        const string matchId = "stamp-duel";
        const string nonce = "stamp-duel-nonce";
        var challenger = Bred("chal", 20, 180, 150, "a");
        var defender = Bred("def", 20, 160, 190, "b");

        BattleResult? onResult = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, matchId, challenger.Id, defender.Id, nonce);
            var on = BattleEngine.Fight(challenger, defender, e, Innate);
            if (!HasPassiveBeat(on.Events)) return false;
            onResult = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(onResult);

        var commitment = CommitReveal.Commit(seed!);
        var entropyHex = Convert.ToHexString(
            CommitReveal.DeriveEntropy(seed!, matchId, challenger.Id, defender.Id, nonce));
        var chal = challenger.ToDto();
        var def = defender.ToDto();
        var stamp = GameConfigVersion.Compute(Innate);
        var fight = new FightResponse(onResult!.ToDto(), Convert.ToHexString(seed!), entropyHex, 0, 0,
            chal, def, chal, def, 0, 0, null, stamp);

        // The stamp names rules that are NOT this client's compiled-in default — that is the whole point.
        Assert.NotEqual(GameConfigVersion.Default, fight.ConfigVersion);

        // Resolved under the stamp → verifies.
        var resolved = GameRulesDto.From(Innate).ToGameConfig();
        Assert.NotNull(resolved);
        Assert.Equal(fight.ConfigVersion, GameConfigVersion.Compute(resolved!));
        var (ok, detail) = FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, resolved);
        Assert.True(ok, detail);

        // Ignoring the stamp (the pre-fix path: compile-time GameConfig.Default) → an honest server reads
        // as a cheat. This is the bug, pinned.
        Assert.False(FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight).Ok);
        Assert.False(FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, GameConfig.Default).Ok);
    }

    // ── 2/5: gauntlet (VerifyGauntlet) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gauntlet_VerifiesUnderTheStampedConfig_AndFailsHonestlyWithout()
    {
        const string gauntletId = "stamp-gauntlet";
        const string nonce = "stamp-gauntlet-nonce";
        var hero = Bred("g-hero", 8, 200, 170, "g");

        GauntletRun? onRun = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, gauntletId, hero.Id, nonce);
            var on = Gauntlet.Resolve(hero, e, Innate);
            var off = Gauntlet.Resolve(hero, e, GameConfig.Default);
            // Require a REAL divergence (different waves cleared), not merely a different log — the
            // gauntlet verifier checks waves + capped XP + item, so a same-count run would still verify.
            if (on.WavesCleared == off.WavesCleared) return false;
            if (!on.Waves.Any(w => HasPassiveBeat(w.Result.Events))) return false;
            onRun = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(onRun);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, gauntletId, hero.Id, nonce);
        var snapshot = hero.ToDto();
        var xp = Gauntlet.XpForRun(snapshot.Level, onRun!.WavesCleared);
        var receipt = new ProgressionReceiptDto(
            "gauntlet", gauntletId, hero.Id, "", hero.Id,
            Convert.ToHexString(seed!), nonce, commitment,
            xp, 0, hero.Level, hero.Level, 0, "", "");
        var run = new GauntletRunResponse(
            onRun.WavesCleared, [], xp, hero.Level,
            Gauntlet.RewardItem(entropy, onRun.WavesCleared), null, snapshot,
            Convert.ToHexString(seed!), Convert.ToHexString(entropy), receipt,
            GameConfigVersion.Compute(Innate));

        var resolved = GameRulesDto.From(Innate).ToGameConfig();
        var (ok, detail) = FairnessAudit.VerifyGauntlet(gauntletId, nonce, commitment, run, resolved);
        Assert.True(ok, detail);

        Assert.False(FairnessAudit.VerifyGauntlet(gauntletId, nonce, commitment, run).Ok);
    }

    // ── 3/5: trials (VerifyTrials) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Trials_VerifiesUnderTheStampedConfig_AndFailsHonestlyWithout()
    {
        const string trialsId = "stamp-trials";
        const string nonce = "stamp-trials-nonce";
        const TrialsAffix affix = TrialsAffix.None;
        var hero = Bred("t-hero", 12, 190, 210, "t");

        TrialsRun? onRun = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, trialsId, hero.Id, nonce);
            var on = Trials.Resolve(hero, e, Innate, affix);
            var off = Trials.Resolve(hero, e, GameConfig.Default, affix);
            if (on.WavesCleared == off.WavesCleared) return false;
            if (!on.Waves.Any(w => HasPassiveBeat(w.Result.Events))) return false;
            onRun = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(onRun);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, trialsId, hero.Id, nonce);
        var receipt = new ProgressionReceiptDto(
            "trials", trialsId, hero.Id, "", hero.Id,
            Convert.ToHexString(seed!), nonce, commitment,
            0, onRun!.WavesCleared, hero.Level, hero.Level, 0, "", "");
        var run = new TrialsRunResponse(
            onRun.WavesCleared, [], Trials.TitleFor(onRun.WavesCleared), onRun.WavesCleared, affix.ToString(),
            hero.ToDto(), Convert.ToHexString(seed!), Convert.ToHexString(entropy), receipt,
            GameConfigVersion.Compute(Innate));

        var resolved = GameRulesDto.From(Innate).ToGameConfig();
        var (ok, detail) = FairnessAudit.VerifyTrials(trialsId, nonce, commitment, run, resolved);
        Assert.True(ok, detail);

        Assert.False(FairnessAudit.VerifyTrials(trialsId, nonce, commitment, run).Ok);
    }

    // ── 4/5: squad (VerifySquad) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Squad_VerifiesUnderTheStampedConfig_AndFailsHonestlyWithout()
    {
        const string matchId = "stamp-squad";
        const string nonce = "stamp-squad-nonce";
        var challengers = new List<Hero>
        {
            Bred("c0", 15, 170, 200, "c0"), Bred("c1", 15, 185, 165, "c1"), Bred("c2", 15, 195, 180, "c2"),
        };
        var defenders = new List<Hero>
        {
            Bred("d0", 15, 160, 210, "d0"), Bred("d1", 15, 205, 175, "d1"), Bred("d2", 15, 175, 190, "d2"),
        };

        SquadResult? onResult = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, "squad", matchId, nonce);
            var on = SquadBattle.Resolve(challengers, defenders, e, Innate);
            if (!on.Duels.Any(d => HasPassiveBeat(d.Result.Events))) return false;
            onResult = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(onResult);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, "squad", matchId, nonce);
        var on = onResult!.Value;
        var resultDto = new SquadResultDto(
            on.ChallengerWon, on.ChallengerWins, on.DefenderWins,
            on.Duels.Select(x => new SquadDuelDto(
                x.Slot, challengers[x.Slot].ToDto(), defenders[x.Slot].ToDto(), x.Result.ToDto())).ToList());
        var replay = new SquadReplayDto(
            challengers.Select(h => h.ToDto()).ToList(), defenders.Select(h => h.ToDto()).ToList(),
            resultDto, commitment, Convert.ToHexString(seed!), Convert.ToHexString(entropy), nonce,
            GameConfigVersion.Compute(Innate));

        var resolved = GameRulesDto.From(Innate).ToGameConfig();
        var (ok, detail) = FairnessAudit.VerifySquad(matchId, nonce, commitment, replay, resolved);
        Assert.True(ok, detail);

        Assert.False(FairnessAudit.VerifySquad(matchId, nonce, commitment, replay).Ok);
    }

    // ── 5/5: tournament (VerifyTournament) ─────────────────────────────────────────────────────────

    [Fact]
    public void Tournament_VerifiesUnderTheStampedConfig_AndFailsHonestlyWithout()
    {
        const string id = "stamp-tourney";
        const string nonce = "stamp-tourney-nonce";
        var entrants = Enumerable.Range(0, 6)
            .Select(i => Bred($"e{i}", 15, (byte)(150 + i * 11), (byte)(210 - i * 9), $"e{i}"))
            .ToList();

        TournamentResult? onResult = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, "tournament", id, nonce);
            var on = Tournament.Resolve(entrants, e, Innate);
            var off = Tournament.Resolve(entrants, e, GameConfig.Default);
            // A different champion is the unmistakable divergence the bracket verifier checks first.
            if (on.ChampionId == off.ChampionId) return false;
            if (!on.Matches.Any(m => m.Result is not null && HasPassiveBeat(m.Result.Events))) return false;
            onResult = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(onResult);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, "tournament", id, nonce);
        var entrantDtos = entrants.Select(h => h.ToDto()).ToList();
        var entrantsCommitment = FairnessAudit.ComputeEntrantsCommitment(entrantDtos);
        var on = onResult!.Value;
        var replay = new TournamentReplayDto(
            entrantDtos,
            on.Matches.Where(m => m.Result is not null)
                .Select(m => new TournamentMatchDto(m.Round, m.Index, m.AId, m.BId, m.WinnerId)).ToList(),
            on.ChampionId, commitment, Convert.ToHexString(seed!), Convert.ToHexString(entropy), nonce,
            entrantsCommitment, GameConfigVersion.Compute(Innate));

        var resolved = GameRulesDto.From(Innate).ToGameConfig();
        var (ok, detail) = FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, replay, resolved);
        Assert.True(ok, detail);

        Assert.False(FairnessAudit.VerifyTournament(id, nonce, commitment, entrantsCommitment, replay).Ok);
    }

    // ── the historical-compatibility bar ───────────────────────────────────────────────────────────

    [Fact]
    public void AnUnstampedReplay_StillVerifiesUnderDefault()
    {
        // Every match resolved before stamping existed carries no stamp and ran on GameConfig.Default.
        // Those artifacts must keep verifying untouched — the stamp is additive, never a new requirement.
        const string matchId = "legacy-duel";
        const string nonce = "legacy-nonce";
        var challenger = Bred("lc", 18, 175, 195, "lc");
        var defender = Bred("ld", 18, 200, 165, "ld");

        var seed = SHA256.HashData("legacy"u8.ToArray());
        var commitment = CommitReveal.Commit(seed);
        var entropy = CommitReveal.DeriveEntropy(seed, matchId, challenger.Id, defender.Id, nonce);
        var result = BattleEngine.Fight(challenger, defender, entropy);   // resolved under Default, as history was
        var chal = challenger.ToDto();
        var def = defender.ToDto();
        var fight = new FightResponse(result.ToDto(), Convert.ToHexString(seed),
            Convert.ToHexString(entropy), 0, 0, chal, def, chal, def);

        Assert.Equal("", fight.ConfigVersion);   // trailing optional → an old server's JSON deserializes here
        var (ok, detail) = FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight);
        Assert.True(ok, detail);
    }

    [Fact]
    public void Gen0Starters_ExpressNothing_SoTheyCannotTestThis()
    {
        // Why every fixture above is BRED, pinned so it cannot be "simplified" back into a false green.
        // Genome.NewGen0 clears genes[16..], so a starter expresses no cosmetic trait, grants no innate
        // passive, and never rolls one — an ON-config fight between starters is EVENT-IDENTICAL to a
        // Default one. A suite built on starters therefore stays green whether or not any of the stamp
        // plumbing works; that is exactly how this bug shipped past a 546-green gate.
        var starterA = new Hero
        {
            Id = "s0", OwnerId = "p", Name = "s0", Level = 20,
            Genome = Genome.NewGen0(SHA256.HashData("starter-a"u8.ToArray())),
        };
        var starterB = new Hero
        {
            Id = "s1", OwnerId = "p", Name = "s1", Level = 20,
            Genome = Genome.NewGen0(SHA256.HashData("starter-b"u8.ToArray())),
        };
        Assert.Empty(Traits.InnatePassives(starterA.Genome, Innate));
        Assert.Empty(Traits.InnatePassives(starterB.Genome, Innate));

        // The bred fixtures, by contrast, do express — so the config difference is real for them.
        Assert.NotEmpty(Traits.InnatePassives(Bred("proof", 20, 180, 150, "proof").Genome, Innate));

        // Over a wide seed sweep, starters never diverge between the two configs; the flip is invisible.
        for (var i = 0; i < 100; i++)
        {
            var entropy = CommitReveal.DeriveEntropy(SHA256.HashData(BitConverter.GetBytes(i)), "s", "s");
            var on = BattleEngine.Fight(starterA, starterB, entropy, Innate);
            var off = BattleEngine.Fight(starterA, starterB, entropy, GameConfig.Default);
            Assert.Equal(off.WinnerId, on.WinnerId);
            Assert.Equal(off.Events.Count, on.Events.Count);
        }
    }

    [Fact]
    public void ADefaultRulesOutcome_StampsTheDefaultVersion()
    {
        // A stamped-but-default outcome resolves to the compiled-in constant with no round trip, so a
        // client can verify it offline exactly as it always could.
        Assert.Equal(GameConfigVersion.Default, GameRulesDto.From(GameConfig.Default).Version);
        Assert.NotEqual(GameConfigVersion.Default, GameRulesDto.From(Innate).Version);
    }
}
