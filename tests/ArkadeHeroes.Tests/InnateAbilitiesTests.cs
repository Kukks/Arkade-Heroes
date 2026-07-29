using System.Security.Cryptography;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;   // FairnessAudit + FightResponse

namespace ArkadeHeroes.Tests;

public class InnateAbilitiesTests
{
    // A genome expressing given cosmetic traits at given dominant-gene bytes; all else plain.
    // Stat genes are set to a mid value so the hero has usable HP/attack/speed.
    private static Genome GenomeWith(byte statGenes, params (TraitCategory Cat, byte Val)[] traits)
    {
        var b = new byte[32];
        for (var i = 0; i < 8; i++) b[i] = statGenes;          // stat + skill genes
        foreach (var (cat, val) in traits) b[16 + (int)cat * 2] = val;
        return new Genome(b);
    }

    private static Hero HeroWith(string id, int level, Genome genome) =>
        new() { Id = id, OwnerId = "p", Name = id, Genome = genome, Level = level };

    private static GameConfig Innate =>
        GameConfig.Default with { Combat = GameConfig.Default.Combat with { InnateAbilities = true } };

    [Fact]
    public void InnateStrength_ReadsExpressedTierOfOneCategory()
    {
        // Legendary Aura → the Legendary affinity-ladder bonus; plain → 0; an affinity category → 0.
        Assert.Equal(0.030, Traits.InnateStrength(GenomeWith(128, (TraitCategory.Aura, 255)), TraitCategory.Aura), 6);
        Assert.Equal(0.0, Traits.InnateStrength(GenomeWith(128), TraitCategory.Aura), 6);
        Assert.Equal(0.0, Traits.InnateStrength(GenomeWith(128, (TraitCategory.ElementAffinity, 255)), TraitCategory.ElementAffinity), 6);
    }

    [Fact]
    public void InnateBonuses_DefaultIsRareChanceAndChunkyPayload()
    {
        // The shape of the tuning, not its exact values: every proc is RARE (a Legendary trait — the strongest
        // roll in the game, strength 0.030 — buys well under a coin flip) and its payload is CHUNKY (a real
        // slice of a health bar or of the blow, not a rounding nudge).
        var ib = InnateBonuses.Default;
        const double legendary = 0.030;   // AffinityBonuses.Default.Legendary — the top of the strength ladder
        double Percent(double chance) => legendary * chance * 100;
        foreach (var chance in new[] { ib.ShieldChance, ib.RegenChance, ib.TrueStrikeChance, ib.ThornsChance,
                                       ib.BrandChance, ib.InitiativeChance })
            Assert.InRange(Percent(chance), 5.0, 45.0);   // rare: a proc, never a permanent stat

        Assert.InRange(ib.Ward, 0.15, 0.60);              // soaks a real slice of MaxHp when it fires
        Assert.InRange(ib.Mend, 0.10, 0.50);              // heals a real slice of MaxHp
        Assert.InRange(ib.Reflect, 0.25, 1.00);           // throws back a real slice of the blow
        Assert.InRange(ib.Tick * ib.BrandTurns, 0.05, 0.40);   // the whole brand costs a real slice of MaxHp
        Assert.Equal(3, ib.BrandTurns);
        Assert.Same(InnateBonuses.Default, GameConfig.Default.Combat.InnateOrDefault); // null resolves to Default
    }

    [Fact]
    public void Initiative_StanceProcsASecondActionInTheSameTurn()
    {
        // Stance buys a rare EXTRA ACTION, not a permanent speed edge. Across a deterministic seed sweep the
        // Legendary-Stance hero SOMETIMES resolves two attacks on a single turn, and the identical hero without
        // Stance NEVER does (TurnOrder hands each fighter exactly one slot a turn). "Sometimes, not always" is
        // the whole point of the rework: an always-on version of this would just be a speed stat.
        int TurnsActedTwice(bool stance)
        {
            var count = 0;
            for (var i = 0; i < 120; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var a = HeroWith("a", 20, stance ? GenomeWith(170, (TraitCategory.Stance, 255)) : GenomeWith(170));
                var b = HeroWith("b", 20, GenomeWith(170));
                count += BattleEngine.Fight(a, b, s, Innate).Events
                    .Where(e => e.ActorId == "a" && e.Kind is BattleEventKind.SkillUsed
                                    or BattleEventKind.Missed or BattleEventKind.Dodged)
                    .GroupBy(e => e.Turn).Count(g => g.Count() > 1);
            }
            return count;
        }
        Assert.Equal(0, TurnsActedTwice(stance: false));    // no Stance ⇒ never two actions in one turn
        Assert.True(TurnsActedTwice(stance: true) > 0);     // Stance ⇒ the follow-up action really lands
    }

    [Fact]
    public void TrueStrike_EyesTradesWhiffsForCriticals()
    {
        // Eyes no longer nudges the hit threshold — it procs a TRUE STRIKE that cannot miss, cannot be dodged,
        // and lands critical. Both halves are visible over a deterministic seed sweep: the Eyed attacker whiffs
        // (Missed/Dodged) a strictly smaller share of its swings and crits a strictly larger share of the ones
        // that land. Rates, not raw counts — the two sweeps run different numbers of swings.
        (double Whiff, double Crit) Swings(bool eyes)
        {
            int whiffs = 0, landed = 0, crits = 0;
            for (var i = 0; i < 200; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var atk = HeroWith("atk", 20, eyes ? GenomeWith(140, (TraitCategory.Eyes, 255)) : GenomeWith(140));
                var def = HeroWith("def", 20, GenomeWith(140));
                foreach (var e in BattleEngine.Fight(atk, def, s, Innate).Events.Where(e => e.ActorId == "atk"))
                {
                    if (e.Kind is BattleEventKind.Missed or BattleEventKind.Dodged) whiffs++;
                    else if (e.Kind == BattleEventKind.SkillUsed) { landed++; if (e.Crit) crits++; }
                }
            }
            return (whiffs / (double)(whiffs + landed), crits / (double)landed);
        }
        var plain = Swings(eyes: false);
        var eyed = Swings(eyes: true);
        Assert.True(eyed.Whiff < plain.Whiff, $"whiff rate {eyed.Whiff:F3} !< {plain.Whiff:F3}");   // cannot miss/dodge
        Assert.True(eyed.Crit > plain.Crit, $"crit rate {eyed.Crit:F3} !> {plain.Crit:F3}");        // lands critical
    }

    [Fact]
    public void Ward_AuraProcsAShieldThatSoaksTheBlow()
    {
        // A Legendary-Aura defender occasionally throws up a ward that eats a whole strike. Across a deterministic
        // seed sweep its TOTAL winning HP fraction is strictly higher than the same defender without Aura
        // (identical stats + seeds) — a ward only ever prevents HP loss, it never costs anything.
        var atk = HeroWith("atk", 20, GenomeWith(200));
        double TotalDefHpFrac(bool aura)
        {
            double total = 0;
            for (var i = 0; i < 60; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var def = HeroWith("def", 20, aura ? GenomeWith(180, (TraitCategory.Aura, 255)) : GenomeWith(180));
                var r = BattleEngine.Fight(atk, def, s, Innate);
                if (r.WinnerId == "def") total += (double)r.WinnerRemainingHp / r.WinnerMaxHp;
            }
            return total;
        }
        Assert.True(TotalDefHpFrac(aura: true) > TotalDefHpFrac(aura: false));   // the ward saved HP
    }

    [Fact]
    public void Thorns_CrestReflectsAChunkOfTheBlowAtTheAttacker()
    {
        // A Legendary-Crest defender occasionally counters. When it fires it is CHUNKY — exactly Reflect × the
        // (pre-shield) blow that provoked it, straight off the attacker's HP. Both the counter and the blow it
        // answers are in the log, so the payload is checked as arithmetic on a real proc rather than as a
        // statistical HP-sum: with the counter now costing a draw of its own, the crest and bare sweeps resolve
        // genuinely different fights, and a sum over them measures stream divergence as much as thorns.
        var events = FirstFightWith(BattleEventKind.Thorns,
            HeroWith("atk", 20, GenomeWith(160)), HeroWith("def", 20, GenomeWith(160, (TraitCategory.Crest, 255))));
        Assert.NotNull(events);

        var at = events!.ToList().FindIndex(e => e.Kind == BattleEventKind.Thorns);
        var thorns = events[at];
        // Execute logs the counter first and the blow that provoked it at the end of the same swing.
        var blow = events.Skip(at).First(e => e.Kind == BattleEventKind.SkillUsed && e.ActorId == "atk");
        Assert.Equal(thorns.Turn, blow.Turn);
        Assert.Equal((int)Math.Round(blow.Damage * InnateBonuses.Default.Reflect), thorns.Damage);
        Assert.True(thorns.Damage > 0);
        Assert.Equal("def", thorns.ActorId);    // the crest-bearer is the source…
        Assert.Equal("atk", thorns.TargetId);   // …and the attacker is the one who pays
    }

    [Fact]
    public void Regen_MarkingHealsOverTheFight()
    {
        // A Legendary-Marking hero occasionally mends, healing a quarter of MaxHp at the start of a turn on which
        // it is hurt. Across a deterministic seed sweep its TOTAL winning HP fraction is strictly higher than the
        // same hero WITHOUT Marking (identical stats + seed) — a mend only ever adds HP. A mid stat line (100) at
        // level 10 keeps fights long enough to give the proc turns to land on.
        // (A single seed is too coarse in this mirror — the hero only wins a subset — so we sum over the sweep.)
        var foe = HeroWith("b", 10, GenomeWith(100));
        double TotalHpFrac(bool marking)
        {
            double total = 0;
            for (var i = 0; i < 60; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var a = HeroWith("a", 10, marking ? GenomeWith(100, (TraitCategory.Marking, 255)) : GenomeWith(100));
                var r = BattleEngine.Fight(a, foe, s, Innate);
                if (r.WinnerId == "a") total += (double)r.WinnerRemainingHp / r.WinnerMaxHp;
            }
            return total;
        }
        Assert.True(TotalHpFrac(marking: true) > TotalHpFrac(marking: false));   // regen never lowers own HP
    }

    [Fact]
    public void Brand_SigilBurnsTheTargetOverTime()
    {
        // A Legendary-Sigil attacker occasionally brands its target on a landing hit; the brand then ticks a slice
        // of the target's MaxHp for BrandTurns turns. Against a STRONGER defender (so the defender wins and its
        // remaining HP is readable), the defender's TOTAL winning HP across a seed sweep is strictly lower when
        // the attacker brands than when it does not — the burn is the only difference and only costs HP (and any
        // fight it burns the defender to death simply drops from the winning total, deepening the gap).
        var def = HeroWith("def", 20, GenomeWith(200));   // strong → wins; high MaxHp → burn tick rounds to >= 1
        double TotalDefHpFrac(bool sigil)
        {
            double total = 0;
            for (var i = 0; i < 60; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var atk = HeroWith("atk", 20, sigil ? GenomeWith(140, (TraitCategory.Sigil, 255)) : GenomeWith(140));
                var r = BattleEngine.Fight(atk, def, s, Innate);
                if (r.WinnerId == "def") total += (double)r.WinnerRemainingHp / r.WinnerMaxHp;
            }
            return total;
        }
        Assert.True(TotalDefHpFrac(sigil: true) < TotalDefHpFrac(sigil: false));   // the brand burned the target down
    }

    [Fact]
    public void FlagOff_IsByteIdenticalToTheEngineWithoutPassives()
    {
        // 200 fights under Default (flag OFF) resolve identically whether or not a hero expresses cosmetic traits
        // — the default-safety proof (cosmetic bytes are inert, so no existing replay can shift when the flag is off).
        for (var i = 0; i < 200; i++)
        {
            var s = new byte[32]; s[0] = (byte)i; s[1] = (byte)(i >> 8);
            var fancy = HeroWith("a", 20, GenomeWith(180, (TraitCategory.Aura, 255), (TraitCategory.Sigil, 255),
                (TraitCategory.Crest, 255), (TraitCategory.Marking, 255), (TraitCategory.Eyes, 255), (TraitCategory.Stance, 255)));
            var plainA = HeroWith("a", 20, GenomeWith(180));
            var foe = HeroWith("b", 20, GenomeWith(160));
            var rf = BattleEngine.Fight(fancy, foe, s);   // Default → flag off
            var rp = BattleEngine.Fight(plainA, foe, s);
            Assert.Equal(rp.WinnerId, rf.WinnerId);
            Assert.Equal(rp.WinnerRemainingHp, rf.WinnerRemainingHp);   // exact remaining HP, not just the winner
            Assert.Equal(rp.Events, rf.Events);   // FULL event stream, field-by-field (BattleEvent is a value record):
                                                  // a cosmetic-laden genome is byte-identical to a plain one when off
        }
    }

    [Fact]
    public void FlagOn_WithNoExpressedCosmeticTraits_IsStillByteIdenticalToFlagOff()
    {
        // The mechanism BEHIND the flag-off guarantee, pinned directly. Procs need new RNG draws, and a new draw
        // in the wrong place would silently reshuffle every downstream roll. Every draw site short-circuits on
        // `Chance > 0`, and a hero with no expressed cosmetic trait resolves every chance to 0 — so even with the
        // flag fully ON such a fight must take exactly the draws the pre-proc engine took. If someone later adds
        // an unconditional proc roll, THIS is the test that catches it (the flag-off test would still pass,
        // because both of its heroes would draw the same extra roll).
        for (var i = 0; i < 200; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var a = HeroWith("a", 20, GenomeWith(180));   // plain: stat genes only, no cosmetic traits
            var b = HeroWith("b", 20, GenomeWith(160));
            var off = BattleEngine.Fight(a, b, s);          // Default → flag off
            var on = BattleEngine.Fight(a, b, s, Innate);   // flag on, but nothing to proc
            Assert.Equal(off.WinnerId, on.WinnerId);
            Assert.Equal(off.WinnerRemainingHp, on.WinnerRemainingHp);
            Assert.Equal(off.Events, on.Events);   // FULL event stream, field-by-field — same draws, same fight
        }
    }

    [Fact]
    public void FlagOn_IsDeterministicAcrossRuns()
    {
        var a = HeroWith("a", 20, GenomeWith(180, (TraitCategory.Sigil, 255), (TraitCategory.Aura, 255)));
        var b = HeroWith("b", 20, GenomeWith(160, (TraitCategory.Crest, 255)));
        var seed = new byte[32]; Array.Fill(seed, (byte)11);
        var r1 = BattleEngine.Fight(a, b, seed, Innate);
        var r2 = BattleEngine.Fight(a, b, seed, Innate);
        Assert.Equal(r1.WinnerId, r2.WinnerId);
        Assert.Equal(r1.Events.Count, r2.Events.Count);   // same config + seed → identical replay (verifiable)
    }

    /// <summary>
    /// The mirror win rate of a hero whose ONLY difference from its opponent is one max-roll (Legendary) trait.
    /// Two deliberate properties make the number readable:
    ///   • seeds are SHA256-derived. DeterministicRng is xoshiro256** whose FIRST output depends only on the _s1
    ///     seed word (bytes 8..15), so the s[0]=i style of seed leaves that word zero and makes every fight's
    ///     opening draw 0 — a degenerate stream that hid Eyes almost entirely under the old always-on tuning.
    ///   • the trait alternates sides. In this exact stat mirror TurnOrder falls to the id tiebreak, which hands
    ///     "a" the first move every turn; carrying the trait on "a" every time buys ~0.09 of free win rate that
    ///     has nothing to do with the passive. Alternating cancels it, so an INERT passive scores ~0.500 and the
    ///     number reported here is the passive's own lift.
    /// </summary>
    private static double MirrorWinRate(TraitCategory cat, int n = 400)
    {
        var wins = 0;
        for (var i = 0; i < n; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var bearerIsA = i % 2 == 0;
            var bearerId = bearerIsA ? "a" : "b";
            var bearer = HeroWith(bearerId, 20, GenomeWith(170, (cat, 255)));
            var plain = HeroWith(bearerIsA ? "b" : "a", 20, GenomeWith(170));
            var (x, y) = bearerIsA ? (bearer, plain) : (plain, bearer);
            if (BattleEngine.Fight(x, y, s, Innate).WinnerId == bearerId) wins++;
        }
        return wins / (double)n;
    }

    [Fact]
    public void BalanceProbe_EachProcIsAnEdgeNotATrump()
    {
        // Re-pinned for innate-v3 (rare procs). Measured at InnateBonuses.Default, n=400:
        //   Aura .625  Marking .593  Eyes .593  Crest .608  Sigil .585  Stance .598
        // The band is [0.53, 0.70]: the floor is above the ~0.510 an INERT passive scores, so a passive that
        // quietly stops firing trips this; the ceiling keeps a max-roll cosmetic trait an edge, never a trump.
        // If one breaches the ceiling, lower that passive's *Chance knob in InnateBonuses.Default and re-run.
        // (The old always-on tuning measured Aura .55 / Marking .53 / Eyes .51 / Crest .57 / Sigil .57 /
        //  Stance .51 on a biased probe — those numbers do not carry over and are not comparable.)
        var cats = new[] { TraitCategory.Aura, TraitCategory.Marking, TraitCategory.Eyes,
                           TraitCategory.Crest, TraitCategory.Sigil, TraitCategory.Stance };
        foreach (var cat in cats)
            Assert.InRange(MirrorWinRate(cat), 0.53, 0.70);
    }

    [Fact]
    public void ProcChance_ScalesWithTheExpressedRarityTier()
    {
        // The design claim of the rework: rarity buys the CHANCE, not the payload. A Legendary Aura and a Common
        // Aura ward for exactly the same amount when they fire — the Legendary just fires far more often. Count
        // ward beats over an identical deterministic sweep to see the ladder.
        int Wards(byte auraGene)
        {
            var count = 0;
            for (var i = 0; i < 150; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var def = HeroWith("def", 20, GenomeWith(180, (TraitCategory.Aura, auraGene)));
                count += BattleEngine.Fight(HeroWith("atk", 20, GenomeWith(200)), def, s, Innate)
                    .Events.Count(e => e.Kind == BattleEventKind.ShieldAbsorbed);
            }
            return count;
        }
        var legendary = Wards(255);   // top of the ladder
        var common = Wards(100);      // an expressed but ordinary Aura
        Assert.True(legendary > common, $"legendary wards {legendary} !> common {common}");
        Assert.True(common > 0, "an expressed Common trait must still be able to proc (the 1% floor)");
    }

    // ── rung 2: each passive, when it fires, surfaces as its own beat in the event log (flag on) ──

    // Deterministically returns the event stream of the first fight in a fixed seed sweep that logs `kind`
    // (resolved with InnateAbilities on), or null if none did. These passives fire only in a subset of seeds, so a
    // sweep is the robust way to land one; the search is pure in the seed, so the test stays deterministic. Reusing
    // the Hero instances across seeds is safe — BattleEngine.Fight never mutates its Hero arguments.
    private static IReadOnlyList<BattleEvent>? FirstFightWith(BattleEventKind kind, Hero a, Hero b)
    {
        for (var i = 0; i < 120; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var r = BattleEngine.Fight(a, b, s, Innate);
            if (r.Events.Any(e => e.Kind == kind)) return r.Events;
        }
        return null;
    }

    [Fact]
    public void ShieldAbsorbed_LogsAShieldBeatWhenAuraWardsABlow()
    {
        // A Legendary-Aura defender under attack. When the ward procs it soaks the blow, which logs a
        // ShieldAbsorbed beat on the defender (source == target). It is a PROC now, so sweep for a fight that
        // fires one rather than pinning a single seed.
        var events = FirstFightWith(BattleEventKind.ShieldAbsorbed,
            HeroWith("atk", 20, GenomeWith(220)), HeroWith("def", 20, GenomeWith(120, (TraitCategory.Aura, 255))));
        Assert.NotNull(events);
        Assert.Contains(events!, e => e.Kind == BattleEventKind.ShieldAbsorbed
            && e.ActorId == "def" && e.TargetId == "def" && e.Damage > 0);
    }

    [Fact]
    public void Regenerated_LogsAHealBeatWhenMarkingTicks()
    {
        // A Legendary-Marking hero self-heals at the start of its turn once it has taken damage (a full-HP tick
        // heals 0 and logs nothing), so sweep the Rung-1 regen setup for a fight where the tick actually heals.
        var events = FirstFightWith(BattleEventKind.Regenerated,
            HeroWith("a", 10, GenomeWith(100, (TraitCategory.Marking, 255))), HeroWith("b", 10, GenomeWith(100)));
        Assert.NotNull(events);
        Assert.Contains(events!, e => e.Kind == BattleEventKind.Regenerated
            && e.ActorId == "a" && e.TargetId == "a" && e.Healed > 0);
    }

    [Fact]
    public void Thorns_LogsAReflectBeatAtTheAttacker()
    {
        // A Legendary-Crest defender reflects part of each landed blow; the reflect now logs a Thorns beat whose
        // source is the crest-bearer (def) and whose target is the attacker (atk) that took it.
        var events = FirstFightWith(BattleEventKind.Thorns,
            HeroWith("atk", 20, GenomeWith(160)), HeroWith("def", 20, GenomeWith(160, (TraitCategory.Crest, 255))));
        Assert.NotNull(events);
        Assert.Contains(events!, e => e.Kind == BattleEventKind.Thorns
            && e.ActorId == "def" && e.TargetId == "atk" && e.Damage > 0);
    }

    [Fact]
    public void Burned_LogsABrandTickOnTheBurningHero()
    {
        // A Legendary-Sigil attacker brands its target on a landing hit; the brand ticks at the start of the
        // target's turn, logging a Burned beat whose source is the brander (atk) and whose target is the burning
        // hero (def). A strong defender survives to take at least one tick.
        var events = FirstFightWith(BattleEventKind.Burned,
            HeroWith("atk", 20, GenomeWith(140, (TraitCategory.Sigil, 255))), HeroWith("def", 20, GenomeWith(200)));
        Assert.NotNull(events);
        Assert.Contains(events!, e => e.Kind == BattleEventKind.Burned
            && e.ActorId == "atk" && e.TargetId == "def" && e.Damage > 0);
    }

    [Fact]
    public void VerifyMatch_OnConfigReproducesPassives_ButADefaultOffClientCannot()
    {
        // The keystone: a match resolved with InnateAbilities ON is faithfully re-derivable ONLY by a client that
        // replays under the same config — the shield/regen/thorns/burn beats are part of the event stream a
        // default-off client can never reproduce. This proves VerifyMatch is config-matched AND that the new
        // events are inside the check (VerifyMatch compares the event log field-by-field).
        var challenger = HeroWith("chal", 20, GenomeWith(180, (TraitCategory.Aura, 255), (TraitCategory.Sigil, 255)));
        var defender = HeroWith("def", 20, GenomeWith(160, (TraitCategory.Crest, 255)));
        const string matchId = "innate-m";
        const string nonce = "innate-n";

        // Pick a seed whose ON fight actually fires a passive beat (guaranteed for these genomes over the sweep) —
        // its presence is exactly what a default-off replay cannot reproduce. Derive entropy the way VerifyMatch
        // does: DeriveEntropy(seed, matchId, challengerId, defenderId, nonce).
        byte[] seed = null!;
        BattleResult onResult = null!;
        for (var i = 0; i < 200 && seed is null; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var e = CommitReveal.DeriveEntropy(s, matchId, challenger.Id, defender.Id, nonce);
            var r = BattleEngine.Fight(challenger, defender, e, Innate);
            if (r.Events.Any(ev => ev.Kind is BattleEventKind.ShieldAbsorbed or BattleEventKind.Regenerated
                                          or BattleEventKind.Thorns or BattleEventKind.Burned))
            {
                seed = s;
                onResult = r;
            }
        }
        Assert.NotNull(seed);   // a passive fired under the on-config

        var commitmentHex = CommitReveal.Commit(seed);
        var entropyHex = Convert.ToHexString(CommitReveal.DeriveEntropy(seed, matchId, challenger.Id, defender.Id, nonce));
        var chalSnap = challenger.ToDto();
        var defSnap = defender.ToDto();
        var fr = new FightResponse(onResult.ToDto(), Convert.ToHexString(seed), entropyHex, 0, 0,
            chalSnap, defSnap, chalSnap, defSnap);

        // A client running the SAME on-config reproduces the fight, passive beats and all.
        var onVerdict = FairnessAudit.VerifyMatch(matchId, nonce, commitmentHex, fr, Innate);
        Assert.True(onVerdict.Ok, onVerdict.Detail);
        // A default-off client (config: null → GameConfig.Default) cannot reproduce an on-config fight — it diverges.
        Assert.False(FairnessAudit.VerifyMatch(matchId, nonce, commitmentHex, fr, config: null).Ok);
    }

    // ── rung 3: frontend-facing surfacing — the propagated flag + the passive-derivation helper ──

    [Fact]
    public void GameConfigDto_PropagatesInnateAbilitiesFlag()
    {
        // How the frontend learns whether innate passives are live. Default is OFF (so the badges stay hidden and
        // today's UI is unchanged); flipping the CombatConfig flag on is reflected on the wire DTO the client reads.
        Assert.False(GameConfigDto.From(GameConfig.Default).InnateAbilities);
        var on = GameConfig.Default with { Combat = GameConfig.Default.Combat with { InnateAbilities = true } };
        Assert.True(GameConfigDto.From(on).InnateAbilities);
    }

    [Fact]
    public void InnatePassives_ListsExpressedCosmeticTiers_NeverAffinities()
    {
        // Aura-Legendary (255) + Sigil-Rare (245), plus a Legendary ElementAffinity that must NOT surface: the
        // helper returns exactly the two COSMETIC passives the hero grants, each at its expressed rarity tier.
        var genome = GenomeWith(128, (TraitCategory.Aura, 255), (TraitCategory.Sigil, 245),
            (TraitCategory.ElementAffinity, 255));
        var passives = Traits.InnatePassives(genome);
        Assert.Equal(2, passives.Count);
        Assert.Contains((TraitCategory.Aura, RarityTier.Legendary), passives);
        Assert.Contains((TraitCategory.Sigil, RarityTier.Rare), passives);
        Assert.DoesNotContain(passives, p => Traits.IsAffinity(p.Category));   // affinities never grant an innate passive

        // A plain genome (no expressed cosmetic traits) grants none.
        Assert.Empty(Traits.InnatePassives(GenomeWith(128)));
    }
}
