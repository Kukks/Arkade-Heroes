using ArkadeHeroes.Core.Skills;

namespace ArkadeHeroes.Core.Combat;

public enum BattleEventKind
{
    SkillUsed,
    Missed,
    Dodged,
    Defeated,
    TimeoutDecision,
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
