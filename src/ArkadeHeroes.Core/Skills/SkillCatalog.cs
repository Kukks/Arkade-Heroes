using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Skills;

/// <summary>
/// The fixed v1 skill table. Skill genes index into <see cref="GeneSkills"/>,
/// so which two gene skills a hero can ever learn is decided at breeding time —
/// another axis of the breeding meta.
/// </summary>
public static class SkillCatalog
{
    /// <summary>Universal opener every hero knows from level 1.</summary>
    public static readonly Skill Strike =
        new("strike", "Strike", Power: 40, Accuracy: 100, SkillScaling.Attack, Element: null, CooldownTurns: 0);

    /// <summary>Elemental finisher unlocked at level 9; strikes with the hero's own element.</summary>
    public static readonly Skill ElementalBurst =
        new("burst", "Elemental Burst", Power: 78, Accuracy: 90, SkillScaling.Magic, Element: null, CooldownTurns: 3);

    /// <summary>The 16 gene-indexed skills (skill gene byte mod 16).</summary>
    public static readonly IReadOnlyList<Skill> GeneSkills =
    [
        new("cleave", "Cleave", 55, 95, SkillScaling.Attack, null, 1),
        new("ember-lash", "Ember Lash", 60, 90, SkillScaling.Attack, Element.Ember, 2),
        new("tidal-crush", "Tidal Crush", 60, 90, SkillScaling.Attack, Element.Tide, 2),
        new("gale-dance", "Gale Dance", 45, 100, SkillScaling.Attack, Element.Gale, 1),
        new("stone-bulwark", "Stone Bulwark", 50, 95, SkillScaling.Attack, Element.Terra, 2, SkillEffect.DefenseBreak),
        new("volt-surge", "Volt Surge", 65, 85, SkillScaling.Magic, Element.Volt, 2),
        new("frost-bite", "Frost Bite", 55, 95, SkillScaling.Magic, Element.Frost, 1),
        new("radiant-lance", "Radiant Lance", 70, 85, SkillScaling.Magic, Element.Radiant, 3),
        new("umbral-drain", "Umbral Drain", 50, 90, SkillScaling.Magic, Element.Umbral, 2, SkillEffect.DrainHalf),
        new("war-cry", "War Cry", 35, 100, SkillScaling.Attack, null, 2, SkillEffect.Focus),
        new("arcane-focus", "Arcane Focus", 35, 100, SkillScaling.Magic, null, 2, SkillEffect.Focus),
        new("rend-armor", "Rend Armor", 45, 95, SkillScaling.Attack, null, 2, SkillEffect.DefenseBreak),
        new("leech-strike", "Leech Strike", 45, 95, SkillScaling.Attack, null, 2, SkillEffect.DrainHalf),
        new("comet-fall", "Comet Fall", 80, 75, SkillScaling.Magic, null, 3),
        new("skull-splitter", "Skull Splitter", 80, 75, SkillScaling.Attack, null, 3),
        new("twin-fangs", "Twin Fangs", 52, 95, SkillScaling.Attack, null, 1),
    ];

    /// <summary>The skills a hero of the given genome and level has unlocked, in learn order.</summary>
    public static IReadOnlyList<Skill> SkillsFor(Genome genome, int level)
    {
        var skills = new List<Skill> { Strike };
        if (level >= 3) skills.Add(GeneSkills[genome.SkillGeneA % GeneSkills.Count]);
        if (level >= 6)
        {
            var second = GeneSkills[genome.SkillGeneB % GeneSkills.Count];
            if (!skills.Any(s => s.Id == second.Id)) skills.Add(second);
        }
        if (level >= 9) skills.Add(ElementalBurst);
        return skills;
    }
}
