namespace ArkadeHeroes.Core.Equipment;

/// <summary>One item id per slot.</summary>
public class EquipmentLoadout
{
    private readonly Dictionary<EquipmentSlot, string> _slots = [];

    public IReadOnlyDictionary<EquipmentSlot, string> Slots => _slots;

    public void Equip(Item item) => _slots[item.Slot] = item.Id;

    public bool Unequip(EquipmentSlot slot) => _slots.Remove(slot);

    /// <summary>Resolves equipped item ids against the catalog, skipping unknown ids.</summary>
    public IReadOnlyList<Item> ResolveItems()
        => _slots.Values.Select(ItemCatalog.Find).OfType<Item>().ToList();
}
