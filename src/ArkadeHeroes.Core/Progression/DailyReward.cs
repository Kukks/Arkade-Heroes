namespace ArkadeHeroes.Core.Progression;

/// <summary>The breakdown of a daily claim: base + quest bonus, then a streak multiplier on the sum.</summary>
public readonly record struct DailyRewardBreakdown(long Base, long QuestBonus, int StreakBonusPct, long Total);

/// <summary>Daily reward math — pure, reads the tunables off <see cref="GameConfig"/>. The streak
/// multiplier applies to (base + quest bonus); day-1 streak adds 0%, and it caps at the config cap.</summary>
public static class DailyReward
{
    public static DailyRewardBreakdown Compute(GameConfig cfg, int completedQuests, int streak)
    {
        long baseSats   = cfg.DailyBaseSats;
        long questBonus = (long)Math.Max(0, completedQuests) * cfg.DailyQuestBonusSats;
        int streakPct   = Math.Min(Math.Max(0, streak - 1) * cfg.DailyStreakStepPct, cfg.DailyStreakCapPct);
        long gross      = baseSats + questBonus;
        long total      = gross + gross * streakPct / 100;
        return new DailyRewardBreakdown(baseSats, questBonus, streakPct, total);
    }
}
