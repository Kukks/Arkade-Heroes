using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Skills;

public enum SkillScaling
{
    Attack,
    Magic,
}

public enum SkillEffect
{
    None,
    /// <summary>Attacker heals for half the damage dealt.</summary>
    DrainHalf,
    /// <summary>Target loses 12% defense for the rest of the match (stacks up to 3).</summary>
    DefenseBreak,
    /// <summary>Attacker gains 12% to its scaling stat for the rest of the match (stacks up to 3).</summary>
    Focus,
}

/// <summary>
/// A combat skill. <see cref="Element"/> null means the skill strikes with the
/// hero's own genome element.
/// </summary>
public sealed record Skill(
    string Id,
    string Name,
    int Power,
    int Accuracy,
    SkillScaling Scaling,
    Element? Element,
    int CooldownTurns,
    SkillEffect Effect = SkillEffect.None);
