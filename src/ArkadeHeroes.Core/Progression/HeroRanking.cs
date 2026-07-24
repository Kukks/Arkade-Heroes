using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// Total-order rankings over heroes. A ranking that ends on a NON-unique key (name, level, generation) is only
/// PARTIALLY ordered: heroes tied on every key fall back to the caller's enumeration order — which, for the
/// server's <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>-backed store, is not
/// stable across requests. That silently breaks two contracts: a paginated list's "paging composes globally"
/// (a tied hero can repeat on one page and vanish from the next) and the trustless board claim that anyone
/// recomputes the same ranking. Ending every ranking on the UNIQUE hero id makes it a TOTAL order —
/// deterministic regardless of input order. Ties are common, not rare: names aren't unique (only CUSTOM names
/// are registry-unique; genome-derived names collide) and every gen-0 starter scores rarity 0.
/// </summary>
public static class HeroRanking
{
    /// <summary>
    /// Heroes by trustless rarity score (desc), then level (desc), then name and id (both Ordinal, so the order
    /// is culture-invariant AND total). Backs the paginated <c>/heroes</c> lists, so paging composes globally.
    /// </summary>
    public static IEnumerable<Hero> ByRarity(IEnumerable<Hero> heroes) => heroes
        .OrderByDescending(h => Rarity.Of(h.Genome).Score)
        .ThenByDescending(h => h.Level)
        .ThenBy(h => h.Name, StringComparer.Ordinal)
        .ThenBy(h => h.Id, StringComparer.Ordinal);
}
