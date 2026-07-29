using ArkadeHeroes.Core.Combat;

namespace ArkadeHeroes.Core.Equipment;

/// <summary>
/// Fixed v1 item shop: three tiers per slot, plus a TRINKET line that is matchup-conditional rather than
/// strictly better (see <see cref="CombatShapes"/>).
///
/// Two rules hold this catalog together:
///   * PRICE TIERS GATE ON LEVEL, so the top set is a thing a hero grows into rather than a thing an account
///     buys on day one. Nothing about what a tier is WORTH changes — only when it can first be worn.
///   * THE COUNTERS LIVE ON ONE SLOT. Weapon and armour stay plainly ranked, so the ladder a new player
///     climbs is unchanged and every counter decision is the single, legible question "which charm do I
///     bring". It also keeps the swing a tilt: counters ADD across slots, so putting them on all three would
///     make a full anti-shape set ±60% and a switch.
/// </summary>
public static class ItemCatalog
{
    /// <summary>The hero level each price tier may first be equipped at.</summary>
    private const int Tier1Level = 1, Tier2Level = 5, Tier3Level = 10;

    public static readonly IReadOnlyList<Item> All =
    [
        // Weapons
        new("rusty-blade", "Rusty Blade", EquipmentSlot.Weapon, new StatMods(Attack: 4), 500, Tier1Level),
        new("steel-saber", "Steel Saber", EquipmentSlot.Weapon, new StatMods(Attack: 10, CritPercent: 2, Speed: 2), 2_500, Tier2Level),
        new("arkforged-edge", "Arkforged Edge", EquipmentSlot.Weapon, new StatMods(Attack: 18, Magic: 6, CritPercent: 4, Speed: -3, MaxHp: -15), 10_000, Tier3Level),
        // Armor
        new("padded-vest", "Padded Vest", EquipmentSlot.Armor, new StatMods(MaxHp: 20, Defense: 3), 500, Tier1Level),
        new("chain-hauberk", "Chain Hauberk", EquipmentSlot.Armor, new StatMods(MaxHp: 45, Defense: 8, Speed: -2), 2_500, Tier2Level),
        new("covenant-plate", "Covenant Plate", EquipmentSlot.Armor, new StatMods(MaxHp: 90, Defense: 14, Speed: -4), 10_000, Tier3Level),
        // Trinkets — the plain ladder
        new("lucky-feather", "Lucky Feather", EquipmentSlot.Trinket, new StatMods(CritPercent: 5), 500, Tier1Level),
        new("swift-anklet", "Swift Anklet", EquipmentSlot.Trinket, new StatMods(Speed: 8), 2_500, Tier2Level),
        new("vtxo-charm", "VTXO Charm", EquipmentSlot.Trinket, new StatMods(MaxHp: 25, Magic: 8, CritPercent: 3), 10_000, Tier3Level),

        // ── Trinkets — the COUNTER line. Same slot, same tier-3 price, deliberately a shade lighter on flat
        //    stats than the VTXO Charm: what you buy instead is a ±20% damage swing that only the MATCHUP
        //    decides. Against a pool split roughly three ways each of these is strong about a third of the
        //    time and weak about a third, so none of them — and not the Charm either — is the best pick, and
        //    the endgame answer is a shelf of four rather than one purchase. Inert until
        //    CombatConfig.GearCounters is switched on.
        new("bulwark-ward", "Bulwark Ward", EquipmentSlot.Trinket, new StatMods(MaxHp: 28, Defense: 6), 10_000,
            Tier3Level, Counters: CombatShape.Offense),   // blunts big hitters; no answer to a grinding tank
        new("sunder-sigil", "Sunder Sigil", EquipmentSlot.Trinket, new StatMods(Attack: 8, Magic: 8, CritPercent: 3), 10_000,
            Tier3Level, Counters: CombatShape.Bulk),      // opens armour; too heavy to catch a speedster
        new("snare-loop", "Snare Loop", EquipmentSlot.Trinket, new StatMods(MaxHp: 12, Defense: 5, Speed: 10), 10_000,
            Tier3Level, Counters: CombatShape.Tempo),     // catches speedsters; nothing stops a straight hit

        // ── Trinket — the WILDCARD. Buys no edge whatsoever: it only widens the wearer's own damage roll from
        //    ±10% to ±35%, mean-preserving. Worth bringing as the underdog (an unlikely fight needs an
        //    unlikely run of rolls) and worth leaving at home as the favourite — the first item in the game
        //    whose answer depends on whether you are ahead rather than on how much you spent.
        new("chaos-prism", "Chaos Prism", EquipmentSlot.Trinket, new StatMods(MaxHp: 25, Magic: 7, CritPercent: 3), 10_000,
            Tier3Level, VarianceBonus: 25),
    ];

    public static Item? Find(string id) => All.FirstOrDefault(i => i.Id == id);
}
