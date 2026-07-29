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
    long PriceSats,
    /// <summary>The hero level this item may first be EQUIPPED at (1 = no gate). Purely a server-side equip
    /// rule — <see cref="StatMods"/> and the resolver never read it, so it is not part of the config stamp and
    /// a replay of an already-equipped loadout is unaffected. It exists so a whale cannot buy a level-1 hero
    /// straight into the top set: it delays convergence on tier-3 without lowering what tier-3 is worth.</summary>
    int MinLevel = 1,
    /// <summary>The build shape this item COUNTERS, or null for a plain stat item. Worth +Edge damage against
    /// an opponent of that shape and -Edge against the shape that answers it — see
    /// <see cref="Combat.CombatShapes"/>. Only read when <see cref="CombatConfig.GearCounters"/> is on.</summary>
    Combat.CombatShape? Counters = null,
    /// <summary>Whole percent this item ADDS to its wearer's own damage-roll half-width, on top of the stock
    /// ±<see cref="CombatConfig.BaseVarianceSpan"/>%. Mean-preserving: it buys no edge, only uncertainty.
    /// Only read when <see cref="CombatConfig.GearCounters"/> is on.</summary>
    int VarianceBonus = 0);
