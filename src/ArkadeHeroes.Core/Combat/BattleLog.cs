using ArkadeHeroes.Core.Skills;

namespace ArkadeHeroes.Core.Combat;

public enum BattleEventKind
{
    SkillUsed,
    Missed,
    Dodged,
    Defeated,
    TimeoutDecision,
    // ── innate-v2 (rung 2) passive beats ── appended so the existing ordinals stay put. Each is emitted ONLY
    // when CombatConfig.InnateAbilities is on AND the effect is non-zero, so a flag-off fight logs none of them
    // (its event stream is byte-identical to the pre-passives engine). Convention: ActorId = the hero whose
    // passive fired (the source), TargetId = the hero whose HP the effect changed — self-effects have both equal.
    ShieldAbsorbed,   // Aura: the defender's shield pool soaked part of an incoming blow
    Regenerated,      // Marking: the hero self-heals at the start of its turn
    Thorns,           // Crest: part of a blow reflected back at the attacker
    Burned,           // Sigil: a brand DoT tick on the branded hero
}

/// <summary>
/// One replayable step of a battle. <see cref="Effect"/> is the status effect the skill applied this
/// step (buff/debuff/drain), so a replay can call out a distinct "Focus up!"/"Defense down!" beat —
/// it is descriptive only and is NOT part of match verification (<c>FairnessAudit.VerifyMatch</c>
/// compares the outcome fields, not this).
/// </summary>
public sealed record BattleEvent(
    int Turn,
    string ActorId,
    string TargetId,
    BattleEventKind Kind,
    string SkillId,
    int Damage,
    bool Crit,
    int Healed,
    int TargetHpAfter,
    string? Note = null,
    SkillEffect Effect = SkillEffect.None);

/// <summary>The full deterministic outcome of a match.</summary>
public sealed record BattleResult(
    string WinnerId,
    string LoserId,
    int Turns,
    IReadOnlyList<BattleEvent> Events,
    int WinnerRemainingHp,
    int WinnerMaxHp);
