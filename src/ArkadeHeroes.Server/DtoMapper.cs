using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Core.Skills;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Server;

public static class DtoMapper
{
    public static FeeInvoiceDto ToDto(this Chain.FeeInvoice invoice)
        => new(invoice.InvoiceId, invoice.PayToAddress, invoice.AmountSats, invoice.Memo);

    public static HeroDto ToDto(this Hero hero, string? commitmentHex = null)
    {
        var items = hero.Equipment.ResolveItems();
        var stats = StatBlock.ComputeFor(hero.Genome, hero.Level, items);
        var skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level);

        return new HeroDto(
            hero.Id,
            hero.Name,
            hero.OwnerId,
            hero.Genome.ToHex(),
            hero.Generation,
            hero.Genome.Element.ToString(),
            hero.Level,
            hero.Xp,
            hero.Level >= Leveling.MaxLevel ? 0 : Leveling.XpToNext(hero.Level),
            new StatsDto(stats.MaxHp, stats.Attack, stats.Magic, stats.Defense,
                stats.Speed, stats.Luck, stats.CritPercent, stats.DodgePercent),
            skills.Select(ToDto).ToList(),
            hero.Equipment.Slots.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            hero.BreedCount,
            hero.BreedCooldownUntil,
            hero.ParentAId,
            hero.ParentBId,
            hero.AssetId,
            hero.MintArkTxId,
            hero.EntropyHex is null && hero.ServerSeedHex is null
                ? null
                : new ProvenanceDto(commitmentHex, hero.ServerSeedHex, hero.PlayerNonce, hero.EntropyHex),
            ToRarityDto(hero.Genome),
            Core.Progression.Sterility.IsSterile(hero.Genome));
    }

    public static RarityDto ToRarityDto(Core.Genetics.Genome genome)
    {
        var r = Core.Progression.Rarity.Of(genome);
        static TraitDto Map(Core.Genetics.TraitVariant t) => new(t.Category.ToString(), t.Value, t.Tier.ToString());
        return new RarityDto(r.Tier.ToString(), r.Score,
            r.Expressed.Select(Map).ToList(), r.CarriedRecessives.Select(Map).ToList());
    }

    public static SkillDto ToDto(this Skill skill) => new(
        skill.Id, skill.Name, skill.Power, skill.Accuracy,
        skill.Scaling.ToString(), skill.Element?.ToString(),
        skill.CooldownTurns, skill.Effect.ToString());

    public static BattleResultDto ToDto(this BattleResult result) => new(
        result.WinnerId,
        result.LoserId,
        result.Turns,
        result.Events.Select(e => new BattleEventDto(
            e.Turn, e.ActorId, e.TargetId, e.Kind.ToString(), e.SkillId,
            e.Damage, e.Crit, e.Healed, e.TargetHpAfter, e.Note)).ToList(),
        result.WinnerRemainingHp,
        result.WinnerMaxHp);

    public static ItemDto ToDto(this Core.Equipment.Item item) => new(
        item.Id, item.Name, item.Slot.ToString(),
        item.Mods.MaxHp, item.Mods.Attack, item.Mods.Magic,
        item.Mods.Defense, item.Mods.Speed, item.Mods.CritPercent,
        item.PriceSats);
}
