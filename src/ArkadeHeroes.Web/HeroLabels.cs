using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Web;

/// <summary>
/// How a hero is written in a list the player picks FROM. Several of those lists commit real sats or
/// destroy a hero — fuse, death-match, sell — and each renders a hero as name, level and rarity. Heroes
/// bred from one pair routinely match on all three, and a confirmation naming a hero the player owns twice
/// ("X is destroyed permanently") reads as confidently for the wrong one as the right one.
///
/// <para>So a distinguishing tag is appended, but ONLY to labels that are genuinely ambiguous within the
/// list being shown. Ambiguity is a property of the rendered text, not of the name: two same-named heroes
/// at different levels already read differently and need no help. Widening
/// <c>HeroNamer</c> makes new collisions rare; this keeps the ones already minted safe to act on.</para>
/// </summary>
internal static class HeroLabels
{
    /// <summary>The picker line for a hero, disambiguated against the list it is shown in.</summary>
    public static string Option(HeroDto hero, IEnumerable<HeroDto> among)
    {
        var label = Describe(hero);
        var clashing = Clashing(hero, among, h => Describe(h) == label);
        return clashing.Count == 0 ? label : $"{label} · #{Tag(hero, clashing)}";
    }

    /// <summary>The hero's name for prose — a confirmation, a result line — disambiguated the same way.</summary>
    public static string Name(HeroDto hero, IEnumerable<HeroDto> among)
    {
        var clashing = Clashing(hero, among, h => h.Name == hero.Name);
        return clashing.Count == 0 ? hero.Name : $"{hero.Name} #{Tag(hero, clashing)}";
    }

    private static string Describe(HeroDto hero)
        => $"{hero.Name} — lvl {hero.Level} · {hero.Rarity?.Tier ?? "Common"}";

    private static List<HeroDto> Clashing(HeroDto hero, IEnumerable<HeroDto> among, Func<HeroDto, bool> collides)
        => among.Where(h => h.Id != hero.Id && collides(h)).ToList();

    /// <summary>
    /// The shortest tail of the hero's id that separates it from the heroes it clashes with — server-minted,
    /// unique, and the same handle its detail page shows. Lengthened rather than fixed at four characters
    /// because "long enough in practice" is not a guarantee, and this is the text a player destroys a hero by.
    /// </summary>
    private static string Tag(HeroDto hero, List<HeroDto> clashing)
    {
        for (var length = 4; length < hero.Id.Length; length++)
        {
            var tail = hero.Id[^length..];
            if (!clashing.Any(h => h.Id.EndsWith(tail, StringComparison.Ordinal))) return tail;
        }
        return hero.Id;
    }
}
