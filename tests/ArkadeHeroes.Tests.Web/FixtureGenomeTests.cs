using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Skills;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The tripwire on <see cref="Fixtures.GenomeHex"/> — the shared hero fixture's genome.
///
/// <para>It was 32 hex characters, i.e. 16 bytes, against a <see cref="Genome.Size"/> of 32.
/// <see cref="Genome.FromHex"/> does NOT pad or return null on that: <c>Convert.FromHexString</c> happily
/// yields 16 bytes and the <see cref="Genome"/> constructor then THROWS
/// <see cref="ArgumentException"/>. Every consumer catches it — <c>HeroDetail</c> wraps both of its genome
/// reads in a <c>try</c> and degrades to "no passives" / "no shape", and <c>Spar</c> turns it into a fight
/// error — so the failure was invisible and the dependent markup simply never rendered. A test written
/// against that markup could not fail, whatever its subject did.</para>
///
/// <para>These tests exist so that stops being a silent, suite-wide weakening and becomes one loud failure.
/// The first is the tripwire proper; the rest pin the specific properties the fixture's genome was CHOSEN
/// for, so "still parses" cannot quietly decay into "parses, but describes a degenerate hero nothing
/// interesting can be asserted about".</para>
/// </summary>
public class FixtureGenomeTests
{
    private const string HeroId = "hero-fixture";

    private static HeroDto TheHero => Fixtures.Hero(HeroId, "Ashfang");

    private static Genome TheGenome => Genome.FromHex(TheHero.GenomeHex);

    /// <summary>
    /// The tripwire. Read through <see cref="Fixtures.Hero"/> rather than off the constant on purpose: what
    /// every other test consumes is the DTO, so a future edit that re-inlines a literal into the record
    /// initializer has to trip this too.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_Parses()
    {
        var hex = TheHero.GenomeHex;

        // Asserted before the parse so a regression reads as "16 bytes, wanted 32" rather than as an
        // ArgumentException from somewhere inside Core.
        Assert.Equal(Genome.Size * 2, hex.Length);

        // The real assertion: this throws on any wrong length, which is precisely the failure mode that
        // every consumer swallows in production code.
        var genome = Genome.FromHex(hex);
        Assert.Equal(hex, genome.ToHex());
    }

    /// <summary>
    /// The genome and the DTO must tell the same story. <c>HeroDto.Element</c> is a string the server
    /// derives from byte [5]; a fixture whose genome says Frost while its DTO says Ember is the same class
    /// of defect as the short hex — self-contradictory data that no single assertion catches.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_SaysTheElementTheFixtureDeclares()
    {
        var hero = TheHero;
        Assert.Equal(hero.Element, Genome.FromHex(hero.GenomeHex).Element.ToString());
    }

    /// <summary>
    /// Same cross-check for rarity: every dominant trait gene sits below the Uncommon cutoff, so the tier
    /// <see cref="ArkadeHeroes.Core.Progression.Rarity"/> computes agrees with the one the fixture's
    /// <c>RarityDto</c> declares.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_VisibleTierMatchesTheDeclaredRarity()
    {
        var hero = TheHero;
        var computed = ArkadeHeroes.Core.Progression.Rarity.Of(Genome.FromHex(hero.GenomeHex));
        Assert.Equal(hero.Rarity!.Tier, computed.Tier.ToString());
    }

    /// <summary>
    /// The exact innate-passive set, which is the payload <c>HeroDetail</c> draws chips from. Pinned as an
    /// exact sequence rather than "not empty" because the interesting part is the ABSENCE: Aura's dominant
    /// gene is deliberately plain (0), so it exercises the branch <see cref="Traits.InnatePassives"/> skips.
    /// An all-loaded genome would return every cosmetic category and prove nothing about that skip.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_GrantsPassives_AndSkipsItsOnePlainCategory()
    {
        var passives = Traits.InnatePassives(TheGenome);

        Assert.Equal(
            new[]
            {
                TraitCategory.Marking, TraitCategory.Eyes, TraitCategory.Crest,
                TraitCategory.Sigil, TraitCategory.Stance,
            },
            passives.Select(p => p.Category));
    }

    /// <summary>
    /// The build shape, at every level the fixture is used at.
    ///
    /// <para>Two things are being pinned. That it is <b>Tempo</b> at all levels — the classifier is
    /// scale-free, so a fixture whose shape flipped with the <c>level</c> argument that 34 call sites vary
    /// would make any shape assertion a lottery. And that it is not <b>Offense</b>, which is where
    /// <see cref="CombatShapes.Of(StatBlock, GearCounterRules)"/> resolves TIES — a fixture sitting on a tie
    /// would read Offense no matter what the classifier actually computed.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]     // Fixtures.Hero's own default
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(50)]    // XpCurve.Default.MaxLevel
    public void TheSharedFixtureGenome_IsTempoShaped_AtEveryLevel(int level)
    {
        Assert.Equal(CombatShape.Tempo, CombatShapes.Of(TheGenome, level));
    }

    /// <summary>
    /// Not a degenerate statline. The ten genes that decide a build are distinct, and their maximum puts
    /// <see cref="Genome.StatGeneCeiling"/> at the full 255 — so the fixture reads as an ordinary
    /// full-range hero rather than as something the capped recruit mint could have produced, which is the
    /// distinction <c>Gauntlet.GhostFor</c> keys off.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_IsAFullRangeHero_WithDistinctStatGenes()
    {
        var genome = TheGenome;

        Assert.Equal(255, genome.StatGeneCeiling);

        int[] statGenes = [0, 1, 2, 3, 4, 8, 9, 10, 11, 12];
        var values = statGenes.Select(i => genome[i]).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }

    /// <summary>
    /// Two DIFFERENT gene skills. <c>SkillCatalog</c> de-duplicates when both skill genes land on the same
    /// entry, so a fixture whose genes collided would silently give every test hero a one-skill kit and hide
    /// anything that depends on a hero having a choice of move.
    /// </summary>
    [Fact]
    public void TheSharedFixtureGenome_LearnsTwoDistinctGeneSkills()
    {
        // GeneSkillBLevel is 6, so this is the first level at which both gene skills are unlocked.
        var skills = SkillCatalog.SkillsFor(TheGenome, level: 6);

        Assert.Equal(3, skills.Count);   // Strike + gene skill A + gene skill B
        Assert.Equal(skills.Count, skills.Select(s => s.Id).Distinct().Count());
    }

    /// <summary>
    /// The end-to-end tripwire, and the one that would have caught the original defect through the code that
    /// hid it: <c>HeroDetail</c> parsing the fixture's genome for real, inside the <c>try</c> that swallows a
    /// bad one, and rendering the result.
    ///
    /// <para>Needs <c>InnateAbilities</c> ON. The whole passive block is behind that flag and the shared
    /// fixture config publishes it OFF, which is exactly why nothing in this suite reached
    /// <c>Traits.InnatePassives</c> and why a 16-byte genome could sit here undetected. With the flag off
    /// this test would render none of the markup it is about and pass for no reason.</para>
    /// </summary>
    [Fact]
    public void TheHeroPage_DrawsPassiveChipsFromTheFixtureGenome_WhenTheServerPublishesThemOn()
    {
        using var ctx = HeroPage();

        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("innate passives", cut.Markup));

        // The chip titles are "<category> → <passive>", which is unique to this block — asserting on the
        // passive names alone would collide with prose elsewhere on the page.
        Assert.Contains("Marking → Regen", cut.Markup);
        Assert.Contains("Stance → Initiative", cut.Markup);

        // Aura's dominant gene is plain, so it grants nothing and must NOT get a chip.
        Assert.DoesNotContain("Aura → Shield", cut.Markup);
    }

    /// <summary>The hero page loads all-or-nothing, so every route it reads has to answer.</summary>
    private static PageTestContext HeroPage()
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        var chain = Fixtures.ChainInfo() with { Config = Fixtures.Config() with { InnateAbilities = true } };
        ctx.Api.GetFails($"/api/heroes/{HeroId}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{HeroId}", TheHero);
        ctx.Api.Get($"/api/receipts/hero/{HeroId}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get("/api/chain/info", chain);
        ctx.Api.Get($"/api/heroes/{HeroId}/timeline", new HeroTimelineDto(HeroId, [], Complete: true, null));
        ctx.Api.Get("/api/bids", Array.Empty<BidDto>());
        ctx.Api.Get("/api/items", Array.Empty<ItemDto>());
        ctx.Api.Get("/api/items/mine", new Dictionary<string, long>());
        return ctx;
    }
}
