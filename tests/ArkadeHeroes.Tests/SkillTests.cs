using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Skills;

namespace ArkadeHeroes.Tests;

public class SkillTests
{
    private static Genome GenomeWithSkills(byte a, byte b)
    {
        var bytes = new byte[32];
        bytes[6] = a;
        bytes[7] = b;
        return new Genome(bytes);
    }

    [Fact]
    public void Level1KnowsStrikeAndGeneSkill()
    {
        // Gene-A is now learned from level 1 (was level 3), so no hero is ever Strike-only.
        var skills = SkillCatalog.SkillsFor(GenomeWithSkills(3, 9), 1);
        Assert.Equal(2, skills.Count);
        Assert.Equal("strike", skills[0].Id);
        Assert.Equal("gale-dance", skills[1].Id);              // gene-A = GeneSkills[3]
        Assert.DoesNotContain(skills, s => s.Id == "war-cry");  // gene-B stays gated to level 6
    }

    [Fact]
    public void UnlocksFollowLevels()
    {
        var genome = GenomeWithSkills(3, 9);
        Assert.Equal(2, SkillCatalog.SkillsFor(genome, 3).Count);
        Assert.Equal(3, SkillCatalog.SkillsFor(genome, 6).Count);
        Assert.Equal(4, SkillCatalog.SkillsFor(genome, 9).Count);
        Assert.Contains(SkillCatalog.SkillsFor(genome, 9), s => s.Id == "burst");
    }

    [Fact]
    public void DuplicateGeneSkillsAreNotLearnedTwice()
    {
        var genome = GenomeWithSkills(5, 5);
        var skills = SkillCatalog.SkillsFor(genome, 6);
        Assert.Equal(skills.Count, skills.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void GeneSkillMappingIsStable()
    {
        var genome = GenomeWithSkills(17, 0); // 17 % 16 = 1 → ember-lash
        var skills = SkillCatalog.SkillsFor(genome, 3);
        Assert.Equal("ember-lash", skills[1].Id);
    }
}
