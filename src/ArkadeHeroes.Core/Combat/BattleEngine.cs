using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Skills;

namespace ArkadeHeroes.Core.Combat;

/// <summary>
/// Deterministic auto-battler. Given two heroes and a 32-byte match seed
/// (derived by commit–reveal from the server seed plus both players' nonces),
/// the fight replays identically anywhere — the server's claimed outcome is
/// verifiable by re-running this engine client-side.
/// </summary>
public static class BattleEngine
{
    public const int MaxTurns = 60;

    public static BattleResult Fight(Hero a, Hero b, ReadOnlySpan<byte> matchSeed, GameConfig? config = null,
        double advantageA = 1.0, double advantageB = 1.0)
    {
        if (a.Id == b.Id) throw new ArgumentException("A hero cannot fight itself.");
        var cfg = config ?? GameConfig.Default;
        var maxTurns = cfg.Combat.MaxTurns;
        var rng = new DeterministicRng(matchSeed);

        // advantageA/B default to 1.0 (a no-op) for every caller except SquadBattle with team synergy on — a
        // per-side whole-lineup damage multiplier applied like affinity below, so a plain fight is unchanged.
        var fighterA = new FighterState(a, cfg, advantageA);
        var fighterB = new FighterState(b, cfg, advantageB);
        var events = new List<BattleEvent>();

        // End-of-action check, shared by a hero's normal action and by a Stance follow-up: if either side is
        // down, log the Defeated beat and hand back the finished match; otherwise null and the fight goes on.
        BattleResult? Finish(int turn, FighterState actor, FighterState target, string skillId)
        {
            if (target.Hp > 0 && actor.Hp > 0) return null;   // actor can die to the target's thorns this swing
            // If both hit 0 the same swing, the target died to the attack first, so the attacker wins
            // (the target.Hp <= 0 branch is checked first, matching that ordering).
            var (win, lose) = target.Hp <= 0 ? (actor, target) : (target, actor);
            events.Add(new BattleEvent(turn, win.Hero.Id, lose.Hero.Id,
                BattleEventKind.Defeated, skillId, 0, false, 0, 0));
            return new BattleResult(win.Hero.Id, lose.Hero.Id, turn, events, win.Hp, win.Stats.MaxHp);
        }

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            foreach (var (actor, target) in TurnOrder(fighterA, fighterB))
            {
                if (actor.Hp <= 0 || target.Hp <= 0) continue;

                // Marking — Mend: a rare, chunky self-heal rolled at the start of the hero's own turn, and only
                // while it is actually HURT (a full-HP hero would waste the proc, so it is not rolled there).
                // Flag off ⇒ RegenChance is 0 ⇒ short-circuit ⇒ no draw.
                if (actor.RegenChance > 0 && actor.Hp < actor.Stats.MaxHp && rng.Chance(actor.RegenChance))
                {
                    var before = actor.Hp;
                    actor.Hp = Math.Min(actor.Stats.MaxHp, actor.Hp + actor.MendHp);
                    var healed = actor.Hp - before;   // actual gain — clamped at MaxHp
                    if (healed > 0)   // Marking self-heal: source == target (the mending hero)
                        events.Add(new BattleEvent(turn, actor.Hero.Id, actor.Hero.Id,
                            BattleEventKind.Regenerated, "", 0, false, healed, actor.Hp));
                }

                // Sigil's brand ticks next — deterministic, RNG-free once applied (the ROLL happened on the
                // attacker's landing hit); a flag-off fight is never branded, so this is inert and logs nothing.
                if (actor.BurnTurnsLeft > 0)
                {
                    var before = actor.Hp;
                    actor.Hp = Math.Max(0, actor.Hp - actor.BurnPerTurn);   // DoT hits HP directly
                    actor.BurnTurnsLeft--;
                    var burned = before - actor.Hp;   // actual HP lost (clamped at 0)
                    if (burned > 0)   // Sigil brand tick: source is the opponent (target) that branded this hero
                        events.Add(new BattleEvent(turn, target.Hero.Id, actor.Hero.Id,
                            BattleEventKind.Burned, "", burned, false, 0, actor.Hp));
                    if (actor.Hp <= 0)   // burned down on its own turn — the opponent wins
                    {
                        events.Add(new BattleEvent(turn, target.Hero.Id, actor.Hero.Id,
                            BattleEventKind.Defeated, "", 0, false, 0, 0));
                        return new BattleResult(target.Hero.Id, actor.Hero.Id, turn, events, target.Hp, target.Stats.MaxHp);
                    }
                }

                actor.TickCooldowns();
                var skill = ChooseSkill(actor, target, cfg);
                Execute(turn, actor, target, skill, rng, events, cfg);
                if (Finish(turn, actor, target, skill.Id) is { } decided) return decided;

                // Stance — Initiative: a rare SECOND action in the same turn — the hero reads the fight and moves
                // again before the opponent can answer. Cooldowns are NOT re-ticked, so the follow-up picks from
                // what is still available after the first cast started its own cooldown (never the same big move
                // twice). Flag off ⇒ InitiativeChance is 0 ⇒ short-circuit ⇒ no draw.
                if (actor.InitiativeChance > 0 && rng.Chance(actor.InitiativeChance))
                {
                    var again = ChooseSkill(actor, target, cfg);
                    Execute(turn, actor, target, again, rng, events, cfg);
                    if (Finish(turn, actor, target, again.Id) is { } decidedAgain) return decidedAgain;
                }
            }
        }

        // Timeout: higher remaining HP fraction wins; exact tie → higher luck; then B (defender advantage).
        var fracA = (double)fighterA.Hp / fighterA.Stats.MaxHp;
        var fracB = (double)fighterB.Hp / fighterB.Stats.MaxHp;
        var (winner, loser) = fracA > fracB ? (fighterA, fighterB)
            : fracB > fracA ? (fighterB, fighterA)
            : fighterA.Stats.Luck > fighterB.Stats.Luck ? (fighterA, fighterB) : (fighterB, fighterA);

        events.Add(new BattleEvent(maxTurns, winner.Hero.Id, loser.Hero.Id,
            BattleEventKind.TimeoutDecision, "", 0, false, 0, loser.Hp,
            $"Timeout after {maxTurns} turns — decided on remaining HP."));
        return new BattleResult(winner.Hero.Id, loser.Hero.Id, maxTurns, events, winner.Hp, winner.Stats.MaxHp);
    }

    private static (FighterState, FighterState)[] TurnOrder(FighterState a, FighterState b)
    {
        // Faster hero acts first; ties broken by luck, then id. RNG-free — Stance's initiative proc buys an EXTRA
        // ACTION on the hero's own turn (see Fight), not a place in this queue, so ordering never draws.
        var aFirst = a.Stats.Speed != b.Stats.Speed
            ? a.Stats.Speed > b.Stats.Speed
            : a.Stats.Luck != b.Stats.Luck
                ? a.Stats.Luck > b.Stats.Luck
                : string.CompareOrdinal(a.Hero.Id, b.Hero.Id) < 0;
        return aFirst ? [(a, b), (b, a)] : [(b, a), (a, b)];
    }

    // Which move a fighter casts this turn. Fully deterministic (a pure function of both fighters'
    // state), so a replay picks the same move — no RNG is drawn here. Tactical play makes status
    // skills worth casting: heal when hurt, land one buff early, soften a target once, else hit hard.
    private static Skill ChooseSkill(FighterState actor, FighterState target, GameConfig cfg)
    {
        var available = actor.Skills.Where(s => actor.CooldownRemaining(s.Id) == 0).ToList();
        if (available.Count == 0) return SkillCatalog.Strike;

        if (cfg.Combat.SelectionPolicy == CombatSelectionPolicy.Tactical)
        {
            // 1. Survive: when hurt past the threshold, prefer a drain skill (it damages AND heals).
            if (actor.Hp * 100 <= actor.Stats.MaxHp * cfg.Combat.HealHpThresholdPercent)
            {
                var drain = BestByDamage(available.Where(s => s.Effect == SkillEffect.DrainHalf), actor, target, cfg);
                if (drain is not null) return drain;
            }
            // 2. Open with a buff: land one Focus stack early, then let it compound the rest of the fight.
            if (actor.FocusStacks == 0)
            {
                var buff = BestByDamage(available.Where(s => s.Effect == SkillEffect.Focus), actor, target, cfg);
                if (buff is not null) return buff;
            }
            // 3. Soften the target: land one DefenseBreak, then swing for full damage after.
            if (target.DefenseBreakStacks == 0)
            {
                var debuff = BestByDamage(available.Where(s => s.Effect == SkillEffect.DefenseBreak), actor, target, cfg);
                if (debuff is not null) return debuff;
            }
        }

        // Default (and Greedy policy): the highest expected-damage move available.
        return BestByDamage(available, actor, target, cfg) ?? SkillCatalog.Strike;
    }

    // The highest expected-damage skill among the candidates, or null if there are none. Ties keep the
    // earlier-learned skill (strict >), so the pick is stable and order-independent of the seed. With
    // ElementAwareSelection ON, the score also folds the element multiplier — the true EV term the
    // resolver already applies (Execute), and the only per-skill factor the base scorer omits.
    private static Skill? BestByDamage(IEnumerable<Skill> candidates, FighterState actor, FighterState target, GameConfig cfg)
    {
        Skill? best = null;
        var bestScore = double.MinValue;
        foreach (var skill in candidates)
        {
            var scale = skill.Scaling == SkillScaling.Attack ? actor.EffectiveAttack : actor.EffectiveMagic;
            var score = skill.Power * scale * (skill.Accuracy / 100.0);
            if (cfg.Combat.ElementAwareSelection)
            {
                var element = skill.Element ?? actor.Hero.Genome.Element;
                score *= ElementMatrix.Multiplier(element, target.Hero.Genome.Element, cfg);
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = skill;
            }
        }
        return best;
    }

    private static void Execute(
        int turn, FighterState actor, FighterState target, Skill skill,
        DeterministicRng rng, List<BattleEvent> events, GameConfig cfg)
    {
        actor.StartCooldown(skill);

        // Eyes — True Strike: a rare blow that finds the weak point. It cannot miss, cannot be dodged, and lands
        // as a critical. Rolled FIRST so it can pre-empt both whiff checks; flag off ⇒ TrueStrikeChance is 0 ⇒
        // the && short-circuits ⇒ no draw, and the two rolls below then run exactly as they always have.
        var trueStrike = actor.TrueStrikeChance > 0 && rng.Chance(actor.TrueStrikeChance);

        if (!trueStrike && !rng.Chance(skill.Accuracy))
        {
            events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
                BattleEventKind.Missed, skill.Id, 0, false, 0, target.Hp));
            return;
        }

        if (!trueStrike && rng.Chance(target.Stats.DodgePercent))
        {
            events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
                BattleEventKind.Dodged, skill.Id, 0, false, 0, target.Hp));
            return;
        }

        var scale = skill.Scaling == SkillScaling.Attack ? actor.EffectiveAttack : actor.EffectiveMagic;
        var element = skill.Element ?? actor.Hero.Genome.Element;
        var elementMult = ElementMatrix.Multiplier(element, target.Hero.Genome.Element, cfg);
        var variance = (90 + rng.Next(21)) / 100.0; // 0.90 .. 1.10
        // `||` evaluates the Chance call first ALWAYS (it is the left operand), so the crit draw is taken on
        // every landed blow exactly as before — True Strike only forces the outcome, it never skips the roll.
        var crit = rng.Chance(actor.Stats.CritPercent) || trueStrike;

        var raw = skill.Power * scale / (target.EffectiveDefense + cfg.Combat.ArmorConstant);
        // The attacker's capped (<=5%) affinity nudge — deterministic (fixed genome),
        // so replays stay verifiable.
        var affinity = Traits.AffinityModifier(actor.Hero.Genome, cfg);
        // (innate-v2 replaced the old single flat cosmetic-trait damage nudge with the six per-category
        //  passives resolved on FighterState; there is no flat innate damage factor here anymore.)
        // Squad team-synergy multiplier — exactly 1.0 (a no-op) outside a synergy-on squad match.
        var damage = Math.Max(1, (int)(raw * elementMult * variance * (crit ? cfg.Combat.CritMultiplier : 1.0) * affinity * actor.Advantage));

        // Aura — Ward: a rare shield thrown up as the blow lands, soaking up to WardHp of THIS strike. Nothing
        // carries to the next blow: it is armour against one strike, not a life buffer, so it reads as a moment.
        // Flag off ⇒ ShieldChance is 0 ⇒ short-circuit ⇒ no draw and ward stays 0 (an unchanged full-damage hit).
        var ward = target.ShieldChance > 0 && rng.Chance(target.ShieldChance) ? target.WardHp : 0;
        var absorbed = target.TakeAttackDamage(damage, ward);
        if (absorbed > 0)   // the defender's ward ate part of the blow (source == target)
            events.Add(new BattleEvent(turn, target.Hero.Id, target.Hero.Id,
                BattleEventKind.ShieldAbsorbed, "", absorbed, false, 0, target.Hp));

        // Crest — Thorns: a rare counter that throws a big slice of the (pre-shield) blow back at the attacker.
        if (target.ThornsChance > 0 && actor.Hp > 0 && rng.Chance(target.ThornsChance))
        {
            var reflected = (int)Math.Round(damage * target.Reflect);   // pre-shield blow — the crest bites back
            if (reflected > 0)
            {
                actor.Hp = Math.Max(0, actor.Hp - reflected);   // DoT/thorns hit HP directly, no ward
                events.Add(new BattleEvent(turn, target.Hero.Id, actor.Hero.Id,   // Crest: reflected at the attacker
                    BattleEventKind.Thorns, "", reflected, false, 0, actor.Hp));
            }
        }

        // Sigil — Brand: a rare DoT seared onto the target, refreshed (never stacked) each time it lands.
        if (actor.BrandChance > 0 && target.Hp > 0 && rng.Chance(actor.BrandChance))
        {
            var per = (int)Math.Round(target.Stats.MaxHp * actor.BrandTick);   // fraction of the TARGET's MaxHp
            if (per > 0) { target.BurnPerTurn = per; target.BurnTurnsLeft = cfg.Combat.InnateOrDefault.BrandTurns; }
        }

        var healed = 0;
        switch (skill.Effect)
        {
            case SkillEffect.DrainHalf:
                healed = (int)(damage * cfg.Combat.DrainFraction);
                actor.Hp = Math.Min(actor.Stats.MaxHp, actor.Hp + healed);
                break;
            case SkillEffect.DefenseBreak:
                target.DefenseBreakStacks = Math.Min(cfg.Combat.MaxEffectStacks, target.DefenseBreakStacks + 1);
                break;
            case SkillEffect.Focus:
                actor.FocusStacks = Math.Min(cfg.Combat.MaxEffectStacks, actor.FocusStacks + 1);
                break;
        }

        events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
            BattleEventKind.SkillUsed, skill.Id, damage, crit, healed, target.Hp,
            elementMult > 1.0 ? "super effective" : elementMult < 1.0 ? "not very effective" : null,
            skill.Effect));
    }

    private sealed class FighterState
    {
        public Hero Hero { get; }
        public StatBlock Stats { get; }
        public IReadOnlyList<Skill> Skills { get; }
        public int Hp { get; set; }
        public int DefenseBreakStacks { get; set; }
        public int FocusStacks { get; set; }

        // ── innate-v2 PROCS — every *Chance is a whole percent and is 0 unless CombatConfig.InnateAbilities is
        //    on AND this hero expresses that category. 0 means the engine never even ROLLS it (each draw site
        //    short-circuits on `Chance > 0`), which is what keeps a flag-off fight draw-for-draw identical. ──
        public int ShieldChance { get; }           // Aura: % per incoming blow to raise a ward…
        public int WardHp { get; }                 //   …soaking up to this much of that one blow
        public int RegenChance { get; }            // Marking: % per own turn while hurt to mend…
        public int MendHp { get; }                 //   …by this much
        public int TrueStrikeChance { get; }       // Eyes: % per attack for an unmissable, undodgeable crit
        public int ThornsChance { get; }           // Crest: % per blow taken to counter…
        public double Reflect { get; }             //   …for this fraction of the blow
        public int BrandChance { get; }            // Sigil: % per landed hit to brand the target…
        public double BrandTick { get; }           //   …for this fraction of the TARGET's MaxHp per tick
        public int InitiativeChance { get; }       // Stance: % per own turn for a second action that turn
        public int BurnPerTurn { get; set; }       // active brand ON this fighter (set by an attacker's Sigil)
        public int BurnTurnsLeft { get; set; }

        /// <summary>A whole-fight damage multiplier (1.0 = none) set by the caller — squad team synergy.</summary>
        public double Advantage { get; }
        private readonly CombatConfig _cfg;
        private readonly Dictionary<string, int> _cooldowns = [];

        public FighterState(Hero hero, GameConfig game, double advantage = 1.0)
        {
            Hero = hero;
            _cfg = game.Combat;
            Advantage = advantage;
            Stats = StatBlock.ComputeFor(hero.Genome, hero.Level, hero.Equipment.ResolveItems());
            Skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level, game.Combat);
            Hp = Stats.MaxHp;

            if (game.Combat.InnateAbilities)
            {
                var ib = game.Combat.InnateOrDefault;
                var g = hero.Genome;
                // The hero's capped strength in a category buys the CHANCE; the knob's magnitude is the payload.
                // A category the hero does not express (or one a config has switched off) resolves to 0% — and a
                // 0% proc is never ROLLED, which is the whole flag-off safety argument. Anything the hero DOES
                // express floors at 1%, so a Common trait is a long shot rather than a chip that lies.
                int Pct(TraitCategory c, double chance)
                {
                    var strength = Traits.InnateStrength(g, c, game);
                    if (strength <= 0 || chance <= 0) return 0;
                    return (int)Math.Clamp(Math.Round(strength * chance * 100), 1, 100);
                }
                ShieldChance = Pct(TraitCategory.Aura, ib.ShieldChance);
                WardHp = (int)Math.Round(Stats.MaxHp * ib.Ward);
                RegenChance = Pct(TraitCategory.Marking, ib.RegenChance);
                MendHp = (int)Math.Round(Stats.MaxHp * ib.Mend);
                TrueStrikeChance = Pct(TraitCategory.Eyes, ib.TrueStrikeChance);
                ThornsChance = Pct(TraitCategory.Crest, ib.ThornsChance);
                Reflect = ib.Reflect;
                BrandChance = Pct(TraitCategory.Sigil, ib.BrandChance);
                BrandTick = ib.Tick;
                InitiativeChance = Pct(TraitCategory.Stance, ib.InitiativeChance);
            }
        }

        /// <summary>Apply an incoming ATTACK's damage: Aura's <paramref name="ward"/> (0 when the proc did not fire)
        /// soaks first, the remainder hits HP; returns the amount the ward ate so the caller can log a
        /// ShieldAbsorbed beat. The ward is scoped to THIS blow — no pool survives it — and DoT/thorns bypass it
        /// and hit HP directly, because it is armour against a strike, not a life buffer.</summary>
        public int TakeAttackDamage(int dealt, int ward)
        {
            var absorbed = Math.Min(ward, dealt);
            Hp = Math.Max(0, Hp - (dealt - absorbed));
            return absorbed;
        }

        public int EffectiveAttack => (int)(Stats.Attack * (1 + _cfg.FocusPerStack * FocusStacks));
        public int EffectiveMagic => (int)(Stats.Magic * (1 + _cfg.FocusPerStack * FocusStacks));
        public int EffectiveDefense => Math.Max(1, (int)(Stats.Defense * (1 - _cfg.DefenseBreakPerStack * DefenseBreakStacks)));

        public int CooldownRemaining(string skillId) => _cooldowns.GetValueOrDefault(skillId);

        public void StartCooldown(Skill skill)
        {
            if (skill.CooldownTurns > 0) _cooldowns[skill.Id] = skill.CooldownTurns + 1;
        }

        public void TickCooldowns()
        {
            foreach (var key in _cooldowns.Keys.ToList())
                if (--_cooldowns[key] <= 0) _cooldowns.Remove(key);
        }
    }
}
