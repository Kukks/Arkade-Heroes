using ArkadeHeroes.Web;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Picker labels. Heroes bred from one pair routinely match on name, level AND rarity — the three fields
/// every picker prints — and the lists this feeds include the ones that burn a hero, stake it to permadeath,
/// or sell it. The rule is that ambiguity is a property of the RENDERED TEXT, so heroes already separable
/// are left alone and only genuine twins pay for a tag.
/// </summary>
public class HeroLabelTests
{
    [Fact]
    public void HeroesThatAlreadyReadDifferentlyAreLeftAlone()
    {
        var a = Fixtures.Hero("hero-aaaa", "Crimson Vanguard Vale");
        var b = Fixtures.Hero("hero-bbbb", "Azure Warden Rook");
        List<ArkadeHeroes.Shared.HeroDto> roster = [a, b];

        Assert.Equal("Crimson Vanguard Vale — lvl 3 · Common", HeroLabels.Option(a, roster));
        Assert.DoesNotContain("#", HeroLabels.Option(b, roster));
    }

    [Fact]
    public void TwinsBothGetATag_AndTheTagsDiffer()
    {
        var a = Fixtures.Hero("hero-aaaa", "Crimson Vanguard Vale");
        var b = Fixtures.Hero("hero-bbbb", "Crimson Vanguard Vale");
        List<ArkadeHeroes.Shared.HeroDto> roster = [a, b];

        var (first, second) = (HeroLabels.Option(a, roster), HeroLabels.Option(b, roster));
        Assert.EndsWith("· #aaaa", first);
        Assert.EndsWith("· #bbbb", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void SameNameAtDifferentLevelsNeedsNoTag()
    {
        var a = Fixtures.Hero("hero-aaaa", "Crimson Vanguard Vale", level: 3);
        var b = Fixtures.Hero("hero-bbbb", "Crimson Vanguard Vale", level: 9);
        List<ArkadeHeroes.Shared.HeroDto> roster = [a, b];

        Assert.DoesNotContain("#", HeroLabels.Option(a, roster));
        Assert.DoesNotContain("#", HeroLabels.Option(b, roster));
    }

    [Fact]
    public void ProseNamesDisambiguateOnTheNameAlone()
    {
        // "X is destroyed permanently" prints no level, so the level cannot be what separates them here.
        var a = Fixtures.Hero("hero-aaaa", "Crimson Vanguard Vale", level: 3);
        var b = Fixtures.Hero("hero-bbbb", "Crimson Vanguard Vale", level: 9);
        List<ArkadeHeroes.Shared.HeroDto> roster = [a, b];

        Assert.Equal("Crimson Vanguard Vale #aaaa", HeroLabels.Name(a, roster));
        Assert.Equal("Crimson Vanguard Vale", HeroLabels.Name(a, [a]));
    }

    [Fact]
    public void AHeroIsNotAmbiguousWithItself()
    {
        var only = Fixtures.Hero("hero-aaaa", "Crimson Vanguard Vale");
        Assert.DoesNotContain("#", HeroLabels.Option(only, [only]));
    }
}
