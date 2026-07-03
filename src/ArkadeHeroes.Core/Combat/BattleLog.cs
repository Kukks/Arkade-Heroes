namespace ArkadeHeroes.Core.Combat;

public enum BattleEventKind
{
    SkillUsed,
    Missed,
    Dodged,
    Defeated,
    TimeoutDecision,
}

/// <summary>One replayable step of a battle.</summary>
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
    string? Note = null);

/// <summary>The full deterministic outcome of a match.</summary>
public sealed record BattleResult(
    string WinnerId,
    string LoserId,
    int Turns,
    IReadOnlyList<BattleEvent> Events,
    int WinnerRemainingHp,
    int WinnerMaxHp);
