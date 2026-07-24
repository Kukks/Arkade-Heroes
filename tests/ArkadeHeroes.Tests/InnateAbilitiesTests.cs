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
    public void InnateBonuses_DefaultIsConservative()
    {
        var ib = InnateBonuses.Default;
        Assert.Equal(3, ib.BrandTurns);
        Assert.True(ib is { Shield: 1.0, Accuracy: 1.0, Thorns: 1.0, Initiative: 1.0, Regen: 0.10, Brand: 0.10 });
        Assert.Same(InnateBonuses.Default, GameConfig.Default.Combat.InnateOrDefault); // null resolves to Default
    }

    [Fact]
    public void Initiative_HigherStanceActsFirstInANearTie()
    {
        // Two heroes with identical stats (same genome stat bytes → identical Speed). Without initiative,
        // TurnOrder falls to the id tiebreak (CompareOrdinal), so "a" (< "b") acts first. Give "b" a Legendary
        // Stance: its effective ordering speed edges ahead, so "b" acts — and lands the first SkillUsed event.
        var plain = GenomeWith(200);                                   // high stats, no traits
        var stanced = GenomeWith(200, (TraitCategory.Stance, 255));    // same stats + Legendary Stance
        var a = HeroWith("a", 20, plain);
        var b = HeroWith("b", 20, stanced);
        var seed = new byte[32]; Array.Fill(seed, (byte)7);

        var firstActor = BattleEngine.Fight(a, b, seed, Innate).Events
            .First(e => e.Kind == BattleEventKind.SkillUsed || e.Kind == BattleEventKind.Missed || e.Kind == BattleEventKind.Dodged)
            .ActorId;
        Assert.Equal("b", firstActor);
    }

    [Fact]
    public void Accuracy_EyesRaisesTheHitThresholdWithoutMovingTheRngStream()
    {
        // Accuracy is threshold-only: DeterministicRng.Chance is Next(100) < clamp(percent), so it draws once
        // regardless of the threshold. Eyes raises the threshold by AccuracyBonus (+3 at Legendary), so a seed
        // whose opening draw lands in [skill.Accuracy, skill.Accuracy + bonus) is a MISS for the plain hero but a
        // HIT for the Eyed hero on the SAME draw. Search deterministically for such a flip seed — its existence
        // proves the bonus moves the compare, not the stream (the identical Next(100) is consumed either way).
        var plain = HeroWith("atk", 20, GenomeWith(140));
        var eyed  = HeroWith("atk", 20, GenomeWith(140, (TraitCategory.Eyes, 255)));
        var def   = HeroWith("def", 20, GenomeWith(140));
        // NOTE: seeds are SHA256-derived (not s[0]=i, s[1]=i>>8). DeterministicRng is xoshiro256** whose FIRST
        // output is a function of the _s1 seed word (bytes 8..15) only; a seed that leaves those bytes zero makes
        // the opening Next(100) draw always 0, so the turn-1 accuracy roll would never miss. A hashed seed varies
        // the opening draw, which is exactly the roll this test needs to land in the flip window.
        byte[] seed = null!;
        for (var i = 0; i < 5000 && seed is null; i++)
        {
            var s = SHA256.HashData(BitConverter.GetBytes(i));
            var plainFirst = BattleEngine.Fight(plain, def, s, Innate).Events[0];
            var eyedFirst  = BattleEngine.Fight(eyed,  def, s, Innate).Events[0];
            if (plainFirst.Kind == BattleEventKind.Missed && eyedFirst.Kind != BattleEventKind.Missed) seed = s;
        }
        Assert.NotNull(seed);                                                            // the lever is real
        Assert.Equal(BattleEventKind.Missed, BattleEngine.Fight(plain, def, seed, Innate).Events[0].Kind);
        Assert.NotEqual(BattleEventKind.Missed, BattleEngine.Fight(eyed, def, seed, Innate).Events[0].Kind); // Eyes flipped it
    }

    [Fact]
    public void Shield_AuraAbsorbsBeforeHp()
    {
        // A Legendary-Aura hero takes strictly LESS HP loss on the first blow than the same hero without Aura,
        // by exactly the shield pool (MaxHp * 0.030 * 1.0). Compare first-blow HP on identical seed + attacker.
        var atk = HeroWith("atk", 20, GenomeWith(220));
        var bare = HeroWith("def", 20, GenomeWith(120));
        var aura = HeroWith("def", 20, GenomeWith(120, (TraitCategory.Aura, 255)));
        var seed = new byte[32]; Array.Fill(seed, (byte)3);

        int FirstDefHp(Hero d) => BattleEngine.Fight(atk, d, seed, Innate).Events
            .First(e => e.Kind == BattleEventKind.SkillUsed && e.TargetId == "def").TargetHpAfter;
        // With a shield, the defender ends the first landed blow with MORE HP than bare (shield ate part of it).
        Assert.True(FirstDefHp(aura) > FirstDefHp(bare));
    }

    [Fact]
    public void Thorns_CrestReflectsPartOfTheBlowAtTheAttacker()
    {
        // A Legendary-Crest defender reflects 3% of each blow at the attacker. The attacker's TOTAL winning HP
        // fraction across a deterministic seed sweep is strictly lower against thorny defenders than against bare
        // ones of identical stats — thorns is the only difference, and reflected damage only ever costs the
        // attacker HP (at stat-gene 160 neither hero rolls a drain skill, so nothing heals it back). A single seed
        // is too coarse here: in this mirror the attacker only wins a subset of fights, so we sum over the sweep.
        var atk = HeroWith("atk", 20, GenomeWith(160));
        double TotalAtkHpFrac(bool crest)
        {
            double total = 0;
            for (var i = 0; i < 60; i++)
            {
                var s = SHA256.HashData(BitConverter.GetBytes(i));
                var def = HeroWith("def", 20, crest ? GenomeWith(160, (TraitCategory.Crest, 255)) : GenomeWith(160));
                var r = BattleEngine.Fight(atk, def, s, Innate);
                if (r.WinnerId == "atk") total += (double)r.WinnerRemainingHp / r.WinnerMaxHp;
            }
            return total;
        }
        Assert.True(TotalAtkHpFrac(crest: true) < TotalAtkHpFrac(crest: false));   // reflected damage cost the attacker HP
    }

    [Fact]
    public void Regen_MarkingHealsOverTheFight()
    {
        // A Legendary-Marking hero regenerates a slice of MaxHp at the start of each of its turns. Across a
        // deterministic seed sweep its TOTAL winning HP fraction is strictly higher than the same hero WITHOUT
        // regen (identical stats + seed) — regen only ever adds HP. A mid stat line (100) at level 10 gives a
        // MaxHp high enough that the per-turn heal rounds to >= 1, while keeping fights long enough for it to tell.
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
        // A Legendary-Sigil attacker brands its target on each landing hit; the brand ticks a slice of the
        // target's MaxHp for BrandTurns turns. Against a STRONGER defender (so the defender wins and its
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

    [Fact]
    public void BalanceProbe_EachPassiveIsANudgeNotASwing()
    {
        // For each passive, 200 equal-level mirror-ish matches (same stat genes, the passive the only difference)
        // must not swing the win rate past a ceiling — a max-roll cosmetic trait is an edge, never a trump.
        var cats = new[] { TraitCategory.Aura, TraitCategory.Marking, TraitCategory.Eyes,
                           TraitCategory.Crest, TraitCategory.Sigil, TraitCategory.Stance };
        foreach (var cat in cats)
        {
            var wins = 0; const int n = 200;
            for (var i = 0; i < n; i++)
            {
                var s = new byte[32]; s[0] = (byte)i; s[1] = (byte)(i >> 8);
                var withTrait = HeroWith("a", 20, GenomeWith(170, (cat, 255)));
                var without   = HeroWith("b", 20, GenomeWith(170));
                if (BattleEngine.Fight(withTrait, without, s, Innate).WinnerId == "a") wins++;
            }
            var rate = wins / (double)n;
            // A single Legendary passive should tilt, not dominate: within [0.50, 0.70]. If a passive exceeds
            // this, lower its InnateBonuses.Default knob and re-run. (id tiebreak gives "a" a hair over 0.5 base.)
            Assert.InRange(rate, 0.50, 0.70);
        }
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
    public void ShieldAbsorbed_LogsAShieldBeatWhenAuraEatsPartOfABlow()
    {
        // Same setup as Shield_AuraAbsorbsBeforeHp: a Legendary-Aura defender under attack. The first landed blow is
        // partly soaked by the shield, which now logs a ShieldAbsorbed beat on the defender (source == target).
        var atk = HeroWith("atk", 20, GenomeWith(220));
        var aura = HeroWith("def", 20, GenomeWith(120, (TraitCategory.Aura, 255)));
        var seed = new byte[32]; Array.Fill(seed, (byte)3);
        var events = BattleEngine.Fight(atk, aura, seed, Innate).Events;
        Assert.Contains(events, e => e.Kind == BattleEventKind.ShieldAbsorbed
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
}
