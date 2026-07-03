namespace ArkadeHeroes.Core.Equipment;

public enum EquipmentSlot
{
    Weapon,
    Armor,
    Trinket,
}

/// <summary>Flat stat modifiers an item grants while equipped.</summary>
public readonly record struct StatMods(
    int MaxHp = 0,
    int Attack = 0,
    int Magic = 0,
    int Defense = 0,
    int Speed = 0,
    int CritPercent = 0)
{
    public static StatMods Sum(IEnumerable<Item>? items)
    {
        var total = new StatMods();
        if (items is null) return total;
        foreach (var item in items)
        {
            var m = item.Mods;
            total = new StatMods(
                total.MaxHp + m.MaxHp,
                total.Attack + m.Attack,
                total.Magic + m.Magic,
                total.Defense + m.Defense,
                total.Speed + m.Speed,
                total.CritPercent + m.CritPercent);
        }
        return total;
    }
}

/// <summary>
/// An equippable item. v1 items are server-side catalog records bought with
/// sats; the design keeps them id-addressed so a later iteration can issue them
/// as Arkade assets (fungible per item type) and trade them banco-style.
/// </summary>
public sealed record Item(
    string Id,
    string Name,
    EquipmentSlot Slot,
    StatMods Mods,
    long PriceSats);
