using System.Security.Cryptography;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The name is what a player picks a hero BY: every picker in the client renders
/// "{name} — lvl {level} · {rarity}", including the ones that burn a hero, stake it to permadeath, or
/// sell it. Two heroes a player cannot tell apart there is a wrong-hero-destroyed bug, so the
/// derivation's job is to separate siblings — while keeping the family resemblance that makes a
/// bloodline readable.
/// </summary>
public class HeroNamerTests
{
    private static (Genome A, Genome B) ParentsFor(int n) =>
        (Genome.NewGen0(BitConverter.GetBytes(n)), Genome.NewGen0(BitConverter.GetBytes(-n - 1)));

    private static string Prefix(string name) => string.Join(' ', name.Split(' ')[..2]);

    [Fact]
    public void ABredChildIsAlmostNeverNamedExactlyAfterAParent()
    {
        // Palette and title are single-byte CROSSOVER regions, so a child takes both from one parent —
        // and therefore that parent's whole name — about half the time when the name is just those two.
        var collisions = 0;
        for (var n = 0; n < 4000; n++)
        {
            var (a, b) = ParentsFor(n);
            var child = GeneMixer.Mix(a, b, SHA256.HashData(BitConverter.GetBytes(n)));
            var name = HeroNamer.DeriveName(child);
            if (name == HeroNamer.DeriveName(a) || name == HeroNamer.DeriveName(b)) collisions++;
        }

        Assert.True(collisions < 400, $"{collisions}/4000 children share a parent's exact name.");
    }

    [Fact]
    public void ABredChildStillShowsItsFamilyPaletteAndTitle()
    {
        // The counterweight to the test above: separating siblings must not be done by randomising the
        // whole name, which would erase the lineage a breeding game is played for.
        var resemblance = 0;
        for (var n = 0; n < 4000; n++)
        {
            var (a, b) = ParentsFor(n);
            var child = GeneMixer.Mix(a, b, SHA256.HashData(BitConverter.GetBytes(n)));
            var prefix = Prefix(HeroNamer.DeriveName(child));
            if (prefix == Prefix(HeroNamer.DeriveName(a)) || prefix == Prefix(HeroNamer.DeriveName(b)))
                resemblance++;
        }

        Assert.InRange(resemblance, 1200, 3600);
    }

    [Fact]
    public void ARosterOfTwentyHeroesRarelyContainsTwoOfTheSameName()
    {
        var rostersWithADuplicate = 0;
        for (var trial = 0; trial < 500; trial++)
        {
            var names = new HashSet<string>();
            for (var h = 0; h < 20; h++)
                names.Add(HeroNamer.DeriveName(Genome.NewGen0(BitConverter.GetBytes(trial * 20 + h))));
            if (names.Count < 20) rostersWithADuplicate++;
        }

        Assert.True(rostersWithADuplicate < 50, $"{rostersWithADuplicate}/500 rosters had a duplicate name.");
    }

    [Fact]
    public void EveryDerivableNameIsOneAPlayerCouldLegallyClaim()
    {
        // Exact, not sampled: the bound is computed from the word lists, so a word too long to be a
        // claimable name fails here rather than minting a hero whose name the rename registry would reject.
        Assert.True(HeroNamer.MaxDerivedNameLength <= NameRegistry.MaxLength,
            $"longest derivable name is {HeroNamer.MaxDerivedNameLength}, cap is {NameRegistry.MaxLength}.");

        for (var n = 0; n < 2000; n++)
        {
            var name = HeroNamer.DeriveName(Genome.NewGen0(BitConverter.GetBytes(n)));
            Assert.Null(NameRegistry.Validate(name, out var normalized));
            Assert.Equal(name, normalized);
        }
    }

    [Fact]
    public void TheSameGenomeAlwaysDerivesTheSameName()
    {
        var genome = Genome.NewGen0([7]);
        Assert.Equal(HeroNamer.DeriveName(genome), HeroNamer.DeriveName(Genome.FromHex(genome.ToHex())));
    }

    [Fact]
    public void GenomesDifferingOnlyOutsideTheAppearanceBytesUsuallyGetDifferentNames()
    {
        // The mark reads the WHOLE genome, so heroes sharing both appearance bytes — the common case among
        // siblings — are still separated by everything else they inherited. Measured over many pairs rather
        // than asserted on one, because any single pair may legitimately land on the same mark.
        var separated = 0;
        for (var n = 0; n < 1000; n++)
        {
            var bytes = Genome.NewGen0(BitConverter.GetBytes(n)).Bytes.ToArray();
            var twin = bytes.ToArray();
            twin[0] ^= 0xFF;   // a stat gene: same appearance bytes, different hero
            if (HeroNamer.DeriveName(new Genome(bytes)) != HeroNamer.DeriveName(new Genome(twin))) separated++;
        }

        Assert.True(separated > 900, $"only {separated}/1000 same-appearance pairs got distinct names.");
    }
}
