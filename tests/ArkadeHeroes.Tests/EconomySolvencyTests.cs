using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// STRUCTURAL solvency of the daily loop. Sats are real BTC and the treasury cannot print them, so the
/// load-bearing invariant is that the biggest faucet — the daily claim — is gated behind actions that cost
/// the player MORE than the claim pays. These are design-level guards on the config: they catch a future
/// tuning change (a fatter quest bonus, a thinner match fee) that would quietly turn the daily into an
/// unfunded faucet, long before it drains a real treasury.
/// </summary>
public class EconomySolvencyTests
{
    /// <summary>The sats a player must pay the treasury to COMPLETE a given daily quest. Unknown ids throw
    /// on purpose: adding a quest to the catalog without pricing it here fails loudly rather than silently
    /// counting as free.</summary>
    private static long QuestFee(string questId, int level, GameConfig cfg) => questId switch
    {
        "duel-win" => Leveling.MatchFee(level, cfg),                                  // a staked match
        "gauntlet" => Gauntlet.Fee(level, cfg),                                       // match fee + premium
        "breed" => cfg.BreedingFeeSats,                                               // flat (escalates with breeds)
        "merge" => cfg.MergeFeeSats,
        "deathmatch" => Leveling.MatchFee(level, cfg) * cfg.DeathMatchFeeMultiplier,
        _ => throw new ArgumentOutOfRangeException(
            nameof(questId), questId, "unpriced daily quest — add its treasury fee to keep the solvency guard honest"),
    };

    [Fact]
    public void DailyLoop_IsTreasuryPositive_ForEveryQuestRotationAndLevel()
    {
        var cfg = GameConfig.Default;
        foreach (var day in Enumerable.Range(0, DailyQuests.Catalog.Count))   // the full rotation of quest sets
        {
            var quests = DailyQuests.ForDay(day, cfg.DailyQuestsPerDay);
            // Worst case for the treasury: a maxed streak, so the claim pays its ceiling.
            var payout = DailyReward.Compute(cfg, quests.Count, streak: 999).Total;

            for (var level = 1; level <= cfg.Curve.MaxLevel; level++)
            {
                var fees = quests.Sum(q => QuestFee(q.Id, level, cfg));
                Assert.True(fees > payout,
                    $"day {day} (quests {string.Join('+', quests.Select(q => q.Id))}) at level {level}: " +
                    $"completing them pays the treasury {fees} sat but the claim pays out {payout} sat — " +
                    "the daily would be a net faucet.");
            }
        }
    }

    [Fact]
    public void QuestlessClaim_IsABoundedDrain()
    {
        // A player who logs in and claims WITHOUT questing is pure outflow — that's intended (a login
        // hook), but it must stay small. Bound it so a config bump can't make the free tier the main faucet.
        var cfg = GameConfig.Default;
        var maxFreeClaim = DailyReward.Compute(cfg, completedQuests: 0, streak: 999).Total;
        Assert.True(maxFreeClaim <= 2 * cfg.DailyBaseSats,
            $"a quest-less daily claim pays up to {maxFreeClaim} sat — an unfunded faucet beyond the streak cap");
    }

    [Fact]
    public void PveGauntlet_CostsMoreThanItsBestDrop()
    {
        // The one PvE path that hands out a real asset: entry must always exceed the BEST item it can drop,
        // so a full-clear farm can never be treasury-negative at any level. Derived from the real reward pool
        // so a future pricier drop trips this, instead of silently beating a stale hardcoded number.
        var cfg = GameConfig.Default;
        var bestDrop = Gauntlet.RewardItems.Max(id => Core.Equipment.ItemCatalog.Find(id)!.PriceSats);
        for (var level = 1; level <= cfg.Curve.MaxLevel; level++)
            Assert.True(Gauntlet.Fee(level, cfg) > bestDrop,
                $"level {level}: gauntlet entry {Gauntlet.Fee(level, cfg)} must exceed its {bestDrop}-sat best drop");
    }
}
