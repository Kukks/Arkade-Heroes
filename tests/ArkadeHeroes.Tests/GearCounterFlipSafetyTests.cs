using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;   // GameOptions + DtoMapper ToDto extensions
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The evidence that turning <c>GameOptions.GearCounters</c> ON is safe for VERIFICATION — the exact
/// counterpart of <see cref="InnateFlipSafetyTests"/>, and written because a green suite CANNOT show it.
///
/// THE BLINDNESS IS DIFFERENT THIS TIME, and that is the whole point of this file. The innate flip was blind
/// to gen-0 starters (<c>Genome.NewGen0</c> clears the trait genes, so no passive is ever rolled). Counters
/// are blind to GEAR: <c>CombatShapes.Multiplier</c> is a product over the wearer's items, so a fixture with
/// NO trinket — or with the same trinket on both sides — multiplies by exactly 1.0 and fights identically on
/// and off. <see cref="AGearlessPopulationCannotSeeTheFlipAtAll"/> measures that directly, so the gap this
/// file closes is a number rather than a worry.
///
/// Every fighter here is therefore a real <c>GeneMixer.Mix</c> child of trait-carrying parents AND wears a
/// VARIED tier-3 loadout drawn across the whole trinket line — the three counter charms, the wildcard prism,
/// and the plain charm — at a level that legitimately clears each item's <c>MinLevel</c> gate. The fixture
/// asserts at runtime that its counters actually FIRE (a non-neutral matchup) rather than trusting that they
/// might. All five resolvable outcome types are covered, each verified through the exact path the SDK uses:
/// serve <see cref="GameRulesDto"/> for the stamp, rebuild, re-hash, replay.
/// </summary>
public class GearCounterFlipSafetyTests
{
    /// <summary>The rules the SERVER now resolves under: GameOptions defaults, i.e. the flip LIVE.</summary>
    private static GameConfig Live { get; } = new GameOptions().ToGameConfig();

    /// <summary>The stamp those rules hash to — provably not the compiled-in default one.</summary>
    private static string Stamp { get; } = GameConfigVersion.Compute(Live);

    /// <summary>The config a client gets by RESOLVING the stamp — rebuilt from the served
    /// <see cref="GameRulesDto"/> exactly as <c>ConfigApi.ResolveAsync</c> does, not by reaching for
    /// <see cref="Live"/> directly.</summary>
    private static GameConfig Resolved { get; } = GameRulesDto.From(Live).ToGameConfig()!;

    /// <summary>The whole tier-3 trinket line, which is where every counter and the wildcard live. Rotating
    /// through it is what makes a matchup non-neutral often enough to be evidence.</summary>
    private static readonly string[] Trinkets =
        ["bulwark-ward", "sunder-sigil", "snare-loop", "chaos-prism", "vtxo-charm"];

    /// <summary>Weapon/armour rotate too, so the population is not one uniform set wearing different charms.</summary>
    private static readonly string[] Weapons = ["arkforged-edge", "steel-saber", "arkforged-edge"];
    private static readonly string[] Armors = ["covenant-plate", "covenant-plate", "chain-hauberk"];

    /// <summary>A trait-carrying breeding parent: usable stat genes drawn over a wide band so children land on
    /// genuinely different build SHAPES, plus all six cosmetic categories (the flip ships alongside innate, so
    /// the fixture fights under both).</summary>
    private static Genome Parent(string seed)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var b = new byte[Genome.Size];
        for (var i = 0; i < 16; i++) b[i] = (byte)(90 + h[i] % 160);
        for (var cat = 0; cat < 6; cat++) b[16 + cat * 2] = (byte)(207 + h[16 + cat] % 49);
        return new Genome(b);
    }

    /// <summary>A genuinely BRED hero wearing a varied tier-3 loadout. Asserts the level clears every equipped
    /// item's <c>MinLevel</c>, so this is a roster the game could actually produce rather than one the test
    /// conjured past its own equip rule.</summary>
    private static Hero Geared(int i, int level)
    {
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData("gear-flip"u8.ToArray()), $"g{i}", "bred");
        var genome = GeneMixer.Mix(Parent($"ga{i}"), Parent($"gb{i}"), entropy);
        var hero = new Hero
        {
            Id = $"g{i}",
            OwnerId = "p",
            Name = $"g{i}",
            Genome = genome,
            Generation = GeneMixer.ChildGeneration(0, 0),
            Level = level,
        };
        foreach (var id in new[] { Weapons[i % Weapons.Length], Armors[i % Armors.Length], Trinkets[i % Trinkets.Length] })
        {
            var item = ItemCatalog.Find(id)!;
            Assert.True(level >= item.MinLevel,
                $"fixture hero g{i} (level {level}) could not legally equip {id} (MinLevel {item.MinLevel})");
            hero.Equipment.Equip(item);
        }
        return hero;
    }

    /// <summary>Levels start at 10 — the tier-3 <c>MinLevel</c> gate — so the counter line is legitimately wearable.</summary>
    private static List<Hero> Population(int count, int baseLevel = 12) =>
        Enumerable.Range(0, count).Select(i => Geared(i, baseLevel + i % 18)).ToList();

    /// <summary>The same hero WITHOUT its trinket — the control that shows what a counter-free fixture sees.</summary>
    private static Hero Trinketless(int i, int level)
    {
        var hero = Geared(i, level);
        hero.Equipment.Unequip(EquipmentSlot.Trinket);
        return hero;
    }

    // ── the switch itself ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFlipIsLiveInConfig_ButCombatConfigDefaultStaysOff()
    {
        // Configuration-driven and defaulting ON, so an operator can turn it back off without a code change —
        // the same shape as InnateAbilities.
        Assert.True(new GameOptions().GearCounters);
        Assert.True(Live.Combat.GearCounters);
        Assert.False(new GameOptions { GearCounters = false }.ToGameConfig().Combat.GearCounters);

        // ...while CombatConfig.Default STAYS off. That constant is what every UNSTAMPED replay is
        // reconstructed under, so flipping it there would silently rewrite what history is checked against.
        Assert.False(CombatConfig.Default.GearCounters);
        Assert.False(GameConfig.Default.Combat.GearCounters);

        // Turning it on genuinely moves the version id — this is the property the whole flip rests on, and it
        // holds only because GameConfigVersion hashes the flag AND the counter knobs.
        Assert.NotEqual(GameConfigVersion.Default, Stamp);
        Assert.NotEqual(
            GameConfigVersion.Compute(GameConfig.Default with
            {
                Combat = GameConfig.Default.Combat with { InnateAbilities = true },
            }),
            Stamp);

        // And the stamp round-trips through the wire the client actually resolves over, knobs included.
        Assert.Equal(Stamp, GameConfigVersion.Compute(Resolved));
        Assert.True(Resolved.Combat.GearCounters);
        Assert.Equal(Live.Combat.CountersOrDefault, Resolved.Combat.CountersOrDefault);
    }

    [Fact]
    public void ThePublishedConfigTellsTheFrontendTheCountersAreLive()
    {
        // The visibility prerequisite: PR #112 gated innate badges on GameConfigDto.InnateAbilities, and a
        // counter the player cannot SEE reads as randomness. The frontend needs the same published flag to
        // decide whether to surface build shape and the counter line at all.
        Assert.True(GameConfigDto.From(Live).GearCounters);
        Assert.False(GameConfigDto.From(GameConfig.Default).GearCounters);
    }

    // ── why a green suite proves nothing here ──────────────────────────────────────────────────────

    [Fact]
    public void AGearlessPopulationCannotSeeTheFlipAtAll()
    {
        // The analogue of the gen-0 blindness that let an earlier innate flip read 546/546 green while being
        // unsafe. With no trinket the counter product is empty (1.0) and the variance span is the stock 10, so
        // ON and OFF draw the identical stream — every event, not merely the winner. A suite whose fixtures
        // are ungeared, or uniformly geared, would pass this flip without ever exercising it.
        var identical = 0;
        for (var i = 0; i < 60; i++)
        {
            var a = Trinketless(i, 12);
            var b = Trinketless(i + 100, 12);
            var seed = SHA256.HashData(Encoding.UTF8.GetBytes($"blind-{i}"));

            var off = BattleEngine.Fight(a, b, seed, GameConfig.Default);
            var on = BattleEngine.Fight(a, b, seed, Live with
            {
                Combat = GameConfig.Default.Combat with { GearCounters = true },
            });

            if (off.WinnerId == on.WinnerId && off.Turns == on.Turns
                && off.Events.Count == on.Events.Count
                && off.Events.SequenceEqual(on.Events)) identical++;
        }
        Assert.Equal(60, identical);
    }

    [Fact]
    public void TheGearedFixtureDoesSeeIt_AndTheCountersActuallyFire()
    {
        // The fixture's own credentials, asserted rather than assumed: it wears counters, it spans all three
        // shapes (so the 3-cycle has a surface), and its matchups are non-neutral often enough to be evidence.
        var heroes = Population(64);

        var carryingCounter = heroes.Count(h => h.Equipment.ResolveItems().Any(i => i.Counters is not null));
        var carryingWildcard = heroes.Count(h =>
            CombatShapes.VarianceSpan(h.Equipment.ResolveItems(), Live) != CombatConfig.BaseVarianceSpan);
        Assert.True(carryingCounter >= 30, $"only {carryingCounter}/64 heroes carry a counter charm");
        Assert.True(carryingWildcard >= 10, $"only {carryingWildcard}/64 heroes carry the wildcard");

        // All three shapes represented — if one were absent its charm would be dead stock in this fixture and
        // a third of the cycle would go untested.
        var shapes = new int[3];
        foreach (var h in heroes) shapes[(int)CombatShapes.Of(h.Genome, h.Level, Live)]++;
        foreach (var c in shapes)
            Assert.True(c > 0, $"a build shape is missing from the fixture: {string.Join('/', shapes)}");

        // ...and the counters FIRE: a matchup where the multiplier is genuinely off 1.0.
        var firing = 0;
        for (var m = 0; m < 400; m++)
        {
            var (a, b) = Pair(heroes, m);
            var shapeB = CombatShapes.Of(b.Genome, b.Level, Live);
            if (Math.Abs(CombatShapes.Multiplier(a.Equipment.ResolveItems(), shapeB, Live) - 1.0) > 1e-9) firing++;
        }
        Assert.True(firing >= 120, $"only {firing}/400 matchups had a non-neutral counter — fixture is near-blind");
    }

    /// <summary>The duel pairing used by every sweep below, so the "counters fire" measurement is about the
    /// same matchups that are then verified.</summary>
    private static (Hero A, Hero B) Pair(List<Hero> heroes, int m)
    {
        var a = heroes[m % heroes.Count];
        var b = heroes[(m * 7 + 13) % heroes.Count];
        if (a.Id == b.Id) b = heroes[(m * 7 + 14) % heroes.Count];
        return (a, b);
    }

    // ── 1/5: duel (VerifyMatch) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Duels_BetweenBredGearedHeroes_AllVerifyUnderTheStamp_WhileAStampBlindClientRejectsMany()
    {
        const int matchups = 400;
        var heroes = Population(64);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var m = 0; m < matchups; m++)
        {
            var (a, b) = Pair(heroes, m);

            var matchId = $"gear-duel-{m}";
            var nonce = $"gear-duel-nonce-{m}";
            var seed = SHA256.HashData(BitConverter.GetBytes(m));
            var commitment = CommitReveal.Commit(seed);
            var entropy = CommitReveal.DeriveEntropy(seed, matchId, a.Id, b.Id, nonce);

            var result = BattleEngine.Fight(a, b, entropy, Live);

            var da = a.ToDto();
            var db = b.ToDto();
            var fight = new FightResponse(result.ToDto(), Convert.ToHexString(seed), Convert.ToHexString(entropy),
                0, 0, da, db, da, db, 0, 0, null, Stamp);

            if (FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, Resolved).Ok) verified++;
            else rejected++;
            if (!FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight).Ok) blindRejected++;
        }

        Assert.Equal(matchups, verified);
        Assert.Equal(0, rejected);

        // The number that makes the blindness concrete: a client replaying under its own compiled-in
        // GameConfig.Default rejects these honest outcomes whenever a counter or the wildcard moved the fight.
        Assert.True(blindRejected >= 200, $"only {blindRejected}/{matchups} duels diverged for a stamp-blind client");
    }

    // ── 2/5: gauntlet (VerifyGauntlet) ─────────────────────────────────────────────────────────────

    [Fact]
    public void GauntletRuns_ByBredGearedHeroes_AllVerifyUnderTheStamp()
    {
        const int runs = 60;
        var heroes = Population(runs);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var i = 0; i < runs; i++)
        {
            var hero = heroes[i];
            var gauntletId = $"gear-gauntlet-{i}";
            var nonce = $"gear-gauntlet-nonce-{i}";
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

        // VerifyGauntlet checks the AGGREGATE (waves cleared + capped XP + item), not every event, so a
        // counter-shifted run that clears the same wave count still reconciles. This understates divergence.
        Assert.True(blindRejected >= 5, $"only {blindRejected}/{runs} runs diverged for a stamp-blind client");
    }

    // ── 3/5: trials (VerifyTrials) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TrialsRuns_ByBredGearedHeroes_AllVerifyUnderTheStamp()
    {
        const int runs = 40;
        const TrialsAffix affix = TrialsAffix.None;
        var heroes = Population(runs);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var i = 0; i < runs; i++)
        {
            var hero = heroes[i];
            var trialsId = $"gear-trials-{i}";
            var nonce = $"gear-trials-nonce-{i}";
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

        Assert.True(blindRejected >= 5, $"only {blindRejected}/{runs} runs diverged for a stamp-blind client");
    }

    // ── 4/5: squad (VerifySquad) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void SquadMatches_BetweenBredGearedLineups_AllVerifyUnderTheStamp()
    {
        const int matches = 40;
        var heroes = Population(48);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var m = 0; m < matches; m++)
        {
            // Disjoint halves: SquadBattle pairs slot against slot, and a hero cannot fight itself.
            var challengers = Enumerable.Range(0, 3).Select(k => heroes[(m * 3 + k) % 24]).ToList();
            var defenders = Enumerable.Range(0, 3).Select(k => heroes[24 + (m * 5 + k) % 24]).ToList();

            var matchId = $"gear-squad-{m}";
            var nonce = $"gear-squad-nonce-{m}";
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

        // Squad checks every duel's full event log across three duels, so divergence is near-certain per match.
        Assert.True(blindRejected >= 30, $"only {blindRejected}/{matches} squad matches diverged for a blind client");
    }

    // ── 5/5: tournament (VerifyTournament) ─────────────────────────────────────────────────────────

    [Fact]
    public void TournamentBrackets_OfBredGearedEntrants_AllVerifyUnderTheStamp()
    {
        const int brackets = 20;
        var heroes = Population(48);

        int verified = 0, rejected = 0, blindRejected = 0;
        for (var t = 0; t < brackets; t++)
        {
            var entrants = Enumerable.Range(0, 6).Select(k => heroes[(t * 6 + k) % heroes.Count]).ToList();

            var id = $"gear-tourney-{t}";
            var nonce = $"gear-tourney-nonce-{t}";
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

        // VerifyTournament checks the champion + the fought bracket, not every event.
        Assert.True(blindRejected >= 5, $"only {blindRejected}/{brackets} brackets diverged for a blind client");
    }

    // ── the historical bar the flip must not move ──────────────────────────────────────────────────

    [Fact]
    public void UnstampedHistory_StillReplaysWithCountersOFF_DespiteTheServerNowRunningThemOn()
    {
        // The property the flip most easily breaks: an outcome resolved BEFORE the flip carries no stamp and
        // ran with counters off. It must keep reconstructing that way no matter what the server runs now —
        // which is exactly why neither GameConfig.Default nor CombatConfig.Default was edited.
        const string matchId = "pre-gear-flip-duel";
        const string nonce = "pre-gear-flip-nonce";
        var a = Geared(900, 18);
        var b = Geared(901, 18);

        var seed = SHA256.HashData("pre-gear-flip"u8.ToArray());
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

        // And these two genuinely disagree under the two configs — the preservation above is a real property,
        // not a tautology about a matchup whose counters happened to be neutral.
        var shapeB = CombatShapes.Of(b.Genome, b.Level, Live);
        Assert.NotEqual(1.0, CombatShapes.Multiplier(a.Equipment.ResolveItems(), shapeB, Live));
        Assert.False(FairnessAudit.VerifyMatch(matchId, nonce, commitment, fight, Resolved).Ok);
    }
}
