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

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            foreach (var (actor, target) in TurnOrder(fighterA, fighterB))
            {
                if (actor.Hp <= 0 || target.Hp <= 0) continue;

                // innate ticks (Marking regen, then Sigil burn) — deterministic, RNG-free, own-turn start. Each logs
                // a beat only when it actually moves HP, so a flag-off fight (both inert) logs nothing.
                if (actor.RegenPerTurn > 0)
                {
                    var before = actor.Hp;
                    actor.Hp = Math.Min(actor.Stats.MaxHp, actor.Hp + actor.RegenPerTurn);
                    var healed = actor.Hp - before;   // actual gain — 0 at full HP, which logs nothing
                    if (healed > 0)   // Marking self-heal: source == target (the regenerating hero)
                        events.Add(new BattleEvent(turn, actor.Hero.Id, actor.Hero.Id,
                            BattleEventKind.Regenerated, "", 0, false, healed, actor.Hp));
                }
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

                if (target.Hp <= 0 || actor.Hp <= 0)   // actor can die to the target's thorns this swing
                {
                    // If both hit 0 the same swing, the target died to the attack first, so the attacker wins
                    // (the target.Hp <= 0 branch is checked first, matching that ordering).
                    var (win, lose) = target.Hp <= 0 ? (actor, target) : (target, actor);
                    events.Add(new BattleEvent(turn, win.Hero.Id, lose.Hero.Id,
                        BattleEventKind.Defeated, skill.Id, 0, false, 0, 0));
                    return new BattleResult(win.Hero.Id, lose.Hero.Id, turn, events, win.Hp, win.Stats.MaxHp);
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
        // Faster hero acts first; ties broken by luck, then id. Stance's initiative passive scales the
        // ordering speed only (never the stat) — a pure double comparison, no RNG. Flag off ⇒ both
        // InitiativeFactor == 1.0, and int×1.0 is exact, so the order is byte-identical to before.
        var aSpeed = a.Stats.Speed * a.InitiativeFactor;
        var bSpeed = b.Stats.Speed * b.InitiativeFactor;
        var aFirst = aSpeed != bSpeed
            ? aSpeed > bSpeed
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

        if (!rng.Chance(skill.Accuracy + actor.AccuracyBonus))   // Eyes: +points; Chance clamps to [0,100], draws once
        {
            events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
                BattleEventKind.Missed, skill.Id, 0, false, 0, target.Hp));
            return;
        }

        if (rng.Chance(target.Stats.DodgePercent))
        {
            events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
                BattleEventKind.Dodged, skill.Id, 0, false, 0, target.Hp));
            return;
        }

        var scale = skill.Scaling == SkillScaling.Attack ? actor.EffectiveAttack : actor.EffectiveMagic;
        var element = skill.Element ?? actor.Hero.Genome.Element;
        var elementMult = ElementMatrix.Multiplier(element, target.Hero.Genome.Element, cfg);
        var variance = (90 + rng.Next(21)) / 100.0; // 0.90 .. 1.10
        var crit = rng.Chance(actor.Stats.CritPercent);

        var raw = skill.Power * scale / (target.EffectiveDefense + cfg.Combat.ArmorConstant);
        // The attacker's capped (<=5%) affinity nudge — deterministic (fixed genome),
        // so replays stay verifiable.
        var affinity = Traits.AffinityModifier(actor.Hero.Genome, cfg);
        // (innate-v2 replaces the old single Traits.InnateModifier damage nudge with the six per-category
        //  passives resolved on FighterState; there is no flat innate damage factor here anymore.)
        // Squad team-synergy multiplier — exactly 1.0 (a no-op) outside a synergy-on squad match.
        var damage = Math.Max(1, (int)(raw * elementMult * variance * (crit ? cfg.Combat.CritMultiplier : 1.0) * affinity * actor.Advantage));

        var absorbed = target.TakeAttackDamage(damage);
        if (absorbed > 0)   // Aura: the defender's shield ate part of the blow (source == target)
            events.Add(new BattleEvent(turn, target.Hero.Id, target.Hero.Id,
                BattleEventKind.ShieldAbsorbed, "", absorbed, false, 0, target.Hp));

        if (target.ThornsFraction > 0 && actor.Hp > 0)
        {
            var reflected = (int)Math.Round(damage * target.ThornsFraction);   // pre-shield blow — the crest bites back
            if (reflected > 0)
            {
                actor.Hp = Math.Max(0, actor.Hp - reflected);   // DoT/thorns hit HP directly, no shield
                events.Add(new BattleEvent(turn, target.Hero.Id, actor.Hero.Id,   // Crest: reflected at the attacker
                    BattleEventKind.Thorns, "", reflected, false, 0, actor.Hp));
            }
        }

        if (actor.BrandStrength > 0 && target.Hp > 0)
        {
            var per = (int)Math.Round(target.Stats.MaxHp * actor.BrandStrength);   // fraction of the TARGET's MaxHp
            if (per > 0) { target.BurnPerTurn = per; target.BurnTurnsLeft = cfg.Combat.InnateOrDefault.BrandTurns; }  // refresh, never stack
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

        // ── innate-v2 passives — all inert (0 / 1.0) unless CombatConfig.InnateAbilities is on ──
        public int ShieldHp { get; set; }          // Aura: one-time absorb pool, consumed before HP
        public int RegenPerTurn { get; }           // Marking: heal at the start of each own turn
        public int AccuracyBonus { get; }          // Eyes: +points to the hit-roll threshold
        public double ThornsFraction { get; }      // Crest: fraction of a blow reflected at the attacker
        public double BrandStrength { get; }       // Sigil: fraction of the TARGET's MaxHp per burn tick
        public double InitiativeFactor { get; }    // Stance: turn-order speed multiplier (>= 1.0)
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
            InitiativeFactor = 1.0;

            if (game.Combat.InnateAbilities)
            {
                var ib = game.Combat.InnateOrDefault;
                var g = hero.Genome;
                double S(TraitCategory c) => Traits.InnateStrength(g, c, game);
                ShieldHp = (int)Math.Round(Stats.MaxHp * S(TraitCategory.Aura) * ib.Shield);
                RegenPerTurn = (int)Math.Round(Stats.MaxHp * S(TraitCategory.Marking) * ib.Regen);
                AccuracyBonus = (int)Math.Round(S(TraitCategory.Eyes) * ib.Accuracy * 100);
                ThornsFraction = S(TraitCategory.Crest) * ib.Thorns;
                BrandStrength = S(TraitCategory.Sigil) * ib.Brand;
                InitiativeFactor = 1.0 + S(TraitCategory.Stance) * ib.Initiative;
            }
        }

        /// <summary>Apply an incoming ATTACK's damage: Aura's shield absorbs first, the remainder hits HP; returns the
        /// amount the shield soaked (0 with no shield) so the caller can log a ShieldAbsorbed beat.
        /// (DoT/thorns bypass the shield and hit HP directly — the shield is armour against blows, not a life buffer.)</summary>
        public int TakeAttackDamage(int dealt)
        {
            var absorbed = Math.Min(ShieldHp, dealt);
            ShieldHp -= absorbed;
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
