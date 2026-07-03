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
    private const int DefenseBreakMaxStacks = 3;
    private const int FocusMaxStacks = 3;

    public static BattleResult Fight(Hero a, Hero b, ReadOnlySpan<byte> matchSeed)
    {
        if (a.Id == b.Id) throw new ArgumentException("A hero cannot fight itself.");
        var rng = new DeterministicRng(matchSeed);

        var fighterA = new FighterState(a);
        var fighterB = new FighterState(b);
        var events = new List<BattleEvent>();

        for (var turn = 1; turn <= MaxTurns; turn++)
        {
            foreach (var (actor, target) in TurnOrder(fighterA, fighterB))
            {
                if (actor.Hp <= 0 || target.Hp <= 0) continue;
                actor.TickCooldowns();
                var skill = ChooseSkill(actor);
                Execute(turn, actor, target, skill, rng, events);

                if (target.Hp <= 0)
                {
                    events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
                        BattleEventKind.Defeated, skill.Id, 0, false, 0, 0));
                    return new BattleResult(actor.Hero.Id, target.Hero.Id, turn, events, actor.Hp, actor.Stats.MaxHp);
                }
            }
        }

        // Timeout: higher remaining HP fraction wins; exact tie → higher luck; then B (defender advantage).
        var fracA = (double)fighterA.Hp / fighterA.Stats.MaxHp;
        var fracB = (double)fighterB.Hp / fighterB.Stats.MaxHp;
        var (winner, loser) = fracA > fracB ? (fighterA, fighterB)
            : fracB > fracA ? (fighterB, fighterA)
            : fighterA.Stats.Luck > fighterB.Stats.Luck ? (fighterA, fighterB) : (fighterB, fighterA);

        events.Add(new BattleEvent(MaxTurns, winner.Hero.Id, loser.Hero.Id,
            BattleEventKind.TimeoutDecision, "", 0, false, 0, loser.Hp,
            $"Timeout after {MaxTurns} turns — decided on remaining HP."));
        return new BattleResult(winner.Hero.Id, loser.Hero.Id, MaxTurns, events, winner.Hp, winner.Stats.MaxHp);
    }

    private static (FighterState, FighterState)[] TurnOrder(FighterState a, FighterState b)
    {
        // Faster hero acts first; ties broken by luck, then by id so order is total.
        var aFirst = a.Stats.Speed != b.Stats.Speed
            ? a.Stats.Speed > b.Stats.Speed
            : a.Stats.Luck != b.Stats.Luck
                ? a.Stats.Luck > b.Stats.Luck
                : string.CompareOrdinal(a.Hero.Id, b.Hero.Id) < 0;
        return aFirst ? [(a, b), (b, a)] : [(b, a), (a, b)];
    }

    private static Skill ChooseSkill(FighterState actor)
    {
        // Deterministic pick: highest expected damage among off-cooldown skills.
        Skill? best = null;
        var bestScore = double.MinValue;
        foreach (var skill in actor.Skills)
        {
            if (actor.CooldownRemaining(skill.Id) > 0) continue;
            var scale = skill.Scaling == SkillScaling.Attack ? actor.EffectiveAttack : actor.EffectiveMagic;
            var score = skill.Power * scale * (skill.Accuracy / 100.0);
            if (score > bestScore)
            {
                bestScore = score;
                best = skill;
            }
        }
        return best ?? SkillCatalog.Strike;
    }

    private static void Execute(
        int turn, FighterState actor, FighterState target, Skill skill,
        DeterministicRng rng, List<BattleEvent> events)
    {
        actor.StartCooldown(skill);

        if (!rng.Chance(skill.Accuracy))
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
        var elementMult = ElementMatrix.Multiplier(element, target.Hero.Genome.Element);
        var variance = (90 + rng.Next(21)) / 100.0; // 0.90 .. 1.10
        var crit = rng.Chance(actor.Stats.CritPercent);

        var raw = skill.Power * scale / (target.EffectiveDefense + 25.0);
        var damage = Math.Max(1, (int)(raw * elementMult * variance * (crit ? 1.5 : 1.0)));

        target.Hp = Math.Max(0, target.Hp - damage);

        var healed = 0;
        switch (skill.Effect)
        {
            case SkillEffect.DrainHalf:
                healed = damage / 2;
                actor.Hp = Math.Min(actor.Stats.MaxHp, actor.Hp + healed);
                break;
            case SkillEffect.DefenseBreak:
                target.DefenseBreakStacks = Math.Min(DefenseBreakMaxStacks, target.DefenseBreakStacks + 1);
                break;
            case SkillEffect.Focus:
                actor.FocusStacks = Math.Min(FocusMaxStacks, actor.FocusStacks + 1);
                break;
        }

        events.Add(new BattleEvent(turn, actor.Hero.Id, target.Hero.Id,
            BattleEventKind.SkillUsed, skill.Id, damage, crit, healed, target.Hp,
            elementMult > 1.0 ? "super effective" : elementMult < 1.0 ? "not very effective" : null));
    }

    private sealed class FighterState
    {
        public Hero Hero { get; }
        public StatBlock Stats { get; }
        public IReadOnlyList<Skill> Skills { get; }
        public int Hp { get; set; }
        public int DefenseBreakStacks { get; set; }
        public int FocusStacks { get; set; }
        private readonly Dictionary<string, int> _cooldowns = [];

        public FighterState(Hero hero)
        {
            Hero = hero;
            Stats = StatBlock.ComputeFor(hero.Genome, hero.Level, hero.Equipment.ResolveItems());
            Skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level);
            Hp = Stats.MaxHp;
        }

        public int EffectiveAttack => (int)(Stats.Attack * (1 + 0.12 * FocusStacks));
        public int EffectiveMagic => (int)(Stats.Magic * (1 + 0.12 * FocusStacks));
        public int EffectiveDefense => Math.Max(1, (int)(Stats.Defense * (1 - 0.12 * DefenseBreakStacks)));

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
