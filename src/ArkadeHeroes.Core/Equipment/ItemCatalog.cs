namespace ArkadeHeroes.Core.Equipment;

/// <summary>Fixed v1 item shop: three tiers per slot.</summary>
public static class ItemCatalog
{
    public static readonly IReadOnlyList<Item> All =
    [
        // Weapons
        new("rusty-blade", "Rusty Blade", EquipmentSlot.Weapon, new StatMods(Attack: 4), 500),
        new("steel-saber", "Steel Saber", EquipmentSlot.Weapon, new StatMods(Attack: 10, CritPercent: 2, Speed: 2), 2_500),
        new("arkforged-edge", "Arkforged Edge", EquipmentSlot.Weapon, new StatMods(Attack: 18, Magic: 6, CritPercent: 4, Speed: -3, MaxHp: -15), 10_000),
        // Armor
        new("padded-vest", "Padded Vest", EquipmentSlot.Armor, new StatMods(MaxHp: 20, Defense: 3), 500),
        new("chain-hauberk", "Chain Hauberk", EquipmentSlot.Armor, new StatMods(MaxHp: 45, Defense: 8, Speed: -2), 2_500),
        new("covenant-plate", "Covenant Plate", EquipmentSlot.Armor, new StatMods(MaxHp: 90, Defense: 14, Speed: -4), 10_000),
        // Trinkets
        new("lucky-feather", "Lucky Feather", EquipmentSlot.Trinket, new StatMods(CritPercent: 5), 500),
        new("swift-anklet", "Swift Anklet", EquipmentSlot.Trinket, new StatMods(Speed: 8), 2_500),
        new("vtxo-charm", "VTXO Charm", EquipmentSlot.Trinket, new StatMods(MaxHp: 25, Magic: 8, CritPercent: 3), 10_000),
    ];

    public static Item? Find(string id) => All.FirstOrDefault(i => i.Id == id);
}
