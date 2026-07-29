using ArkadeHeroes.Core.Content;

namespace ArkadeHeroes.Core.Equipment;

/// <summary>
/// The v1 item shop: three tiers per slot, plus a TRINKET line that is matchup-conditional rather than
/// strictly better (see <see cref="Combat.CombatShapes"/>).
///
/// The items themselves are now AUTHORED DATA — <c>Content/items.json</c>, loaded and validated through
/// <see cref="ContentPack.Default"/> — so releasing or repricing gear is a content edit rather than a code
/// change. This type stays as the lookup every resolver already calls; only where the rows come from moved.
/// Authoring is add-only: a published id is immutable and a "change" means a NEW id, which
/// <see cref="ContentValidation"/> enforces against the seal ledger.
///
/// Three design rules hold the catalog together. They are prose, not code, so they live here rather than in
/// the JSON:
///   * PRICE TIERS GATE ON LEVEL, so the top set is a thing a hero grows into rather than a thing an account
///     buys on day one. Nothing about what a tier is WORTH changes — only when it can first be worn.
///   * THE COUNTERS LIVE ON ONE SLOT. Weapon and armour stay plainly ranked, so the ladder a new player
///     climbs is unchanged and every counter decision is the single, legible question "which charm do I
///     bring". It also keeps the swing a tilt: counters ADD across slots, so putting them on all three would
///     make a full anti-shape set ±60% and a switch.
///   * THE COUNTER CHARMS ARE A SHELF, NOT A LADDER. Bulwark Ward, Sunder Sigil and Snare Loop share the
///     tier-3 price and sit a shade lighter on flat stats than the VTXO Charm; what you buy instead is a
///     ±20% damage swing only the MATCHUP decides, so against a pool split roughly three ways none of them
///     — and not the Charm either — is the best pick. Chaos Prism is the WILDCARD: it buys no edge at all,
///     only a wider, mean-preserving damage roll, so it is worth bringing as the underdog and worth leaving
///     at home as the favourite. All four are inert until <c>CombatConfig.GearCounters</c> is switched on.
/// </summary>
public static class ItemCatalog
{
    /// <summary>Every item in the shipped content pack, in authored order.</summary>
    public static IReadOnlyList<Item> All => ContentPack.Default.Items;

    public static Item? Find(string id) => ContentPack.Default.FindItem(id);
}
