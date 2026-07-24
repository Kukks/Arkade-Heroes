using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

public class HeroRankingTests
{
    // A hero tied with its siblings on every RANKING key: a plain (rarity-0) genome, level 1, the same name.
    // Only the unique id distinguishes them — exactly the gen-0-starter case the paginated /heroes lists span.
    private static Hero Tied(string id) =>
        new() { Id = id, OwnerId = "p", Name = "Dup", Genome = new Genome(new byte[32]), Level = 1 };

    [Fact]
    public void ByRarity_IsTotalOrder_DeterministicRegardlessOfInputOrder()
    {
        var a = Tied("a"); var b = Tied("b"); var c = Tied("c");
        // LINQ OrderBy is STABLE, so a merely partial order would echo the input order — which, for the server's
        // ConcurrentDictionary-backed store, is unstable across requests (breaking "paging composes globally").
        // A TOTAL order (unique id last) ignores input order entirely.
        var forward = HeroRanking.ByRarity(new[] { a, b, c }).Select(h => h.Id).ToList();
        var shuffled = HeroRanking.ByRarity(new[] { c, a, b }).Select(h => h.Id).ToList();
        Assert.Equal(forward, shuffled);                    // same ranking no matter the enumeration order
        Assert.Equal(new[] { "a", "b", "c" }, forward);     // ordered by the unique id tiebreak
    }

    [Fact]
    public void ByRarity_RanksRarityThenLevel_AheadOfTheIdTiebreak()
    {
        // The id tiebreak is LAST — it never overrides the real ranking keys. A rarer hero and a
        // higher-level hero each outrank a plain level-1 hero despite a later-sorting id.
        var plain = Tied("a");
        var rare = new Hero { Id = "z", OwnerId = "p", Name = "Rare", Level = 1, Genome = LegendaryAura() };
        var higher = new Hero { Id = "y", OwnerId = "p", Name = "Hi", Level = 9, Genome = new Genome(new byte[32]) };

        var order = HeroRanking.ByRarity(new[] { plain, rare, higher }).Select(h => h.Id).ToList();
        Assert.Equal("z", order[0]);   // rarest (non-zero score) first
        Assert.Equal("y", order[1]);   // then the higher level (both score 0)
        Assert.Equal("a", order[2]);   // plain level-1 last, despite id "a" sorting before "y"/"z"
    }

    private static Genome LegendaryAura()
    {
        var b = new byte[32];
        b[16 + (int)TraitCategory.Aura * 2] = 255;   // a Legendary expressed trait → a non-zero rarity score
        return new Genome(b);
    }
}
