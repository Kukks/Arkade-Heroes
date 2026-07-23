using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// XP-weighted matchmaking: how evenly matched two heroes are, and what a staked
/// fight between them would swing — read straight off the conserved transfer, so
/// suggestions steer players toward fights where XP is actually at stake (peers),
/// not lopsided ones the clamp zeroes out.
/// </summary>
public static class Matchmaking
{
    /// <summary>How evenly matched two heroes are — smaller is closer. The ranking key for suggested opponents.</summary>
    public static int LevelGap(int heroLevel, int opponentLevel) => Math.Abs(heroLevel - opponentLevel);

    /// <summary>XP the hero would GAIN by beating this opponent (0 when the opponent is far weaker — no farming down the ladder).</summary>
    public static long XpIfWin(int heroLevel, int opponentLevel) => Leveling.XpTransfer(heroLevel, opponentLevel);

    /// <summary>XP the hero would LOSE if it lost to this opponent (0 when the opponent is far stronger — an upset costs the underdog nothing).</summary>
    public static long XpIfLose(int heroLevel, int opponentLevel) => Leveling.XpTransfer(opponentLevel, heroLevel);

    /// <summary>Coarse pre-stake favorability from the challenger's view (combat has variance — no precise %). Threshold 3 levels.</summary>
    public static string Favor(int heroLevel, int opponentLevel) => (heroLevel - opponentLevel) switch
    {
        >= 3 => "favored",
        <= -3 => "underdog",
        _ => "even",
    };

    // ── Power-score matchmaking (F18): same-level heroes vary wildly (traits, gear, rarity) ──

    /// <summary>How far apart two heroes are in realized power, as a percent of the stronger — the
    /// primary matchmaking key. A geared level-8 and a naked level-10 can be a closer fight than LevelGap shows.</summary>
    public static int PowerGapPercent(int heroPower, int opponentPower)
        => (int)Math.Round(Math.Abs(heroPower - opponentPower) * 100.0 / Math.Max(1, Math.Max(heroPower, opponentPower)));

    /// <summary>Coarse favorability from realized POWER <em>and the element matchup</em>, not level — the honest
    /// read where gear is staked (death-match). Each side's power is scaled by the SAME element ring the fight
    /// uses (<see cref="ElementMatrix"/>), so a hard-counter attacker reads favored even at lower raw power —
    /// the ring is the single biggest combat lever, and a power-only read that ignored it labelled a heavy
    /// favourite an underdog. Favored ≥ 1.15× the opponent's effective power, underdog ≤ 0.87×, else even.</summary>
    public static string PowerFavor(int heroPower, int opponentPower, Element heroElement, Element opponentElement,
        GameConfig? config = null)
    {
        var heroEffective = heroPower * ElementMatrix.Multiplier(heroElement, opponentElement, config);
        var opponentEffective = opponentPower * ElementMatrix.Multiplier(opponentElement, heroElement, config);
        var ratio = heroEffective / Math.Max(1.0, opponentEffective);
        return ratio >= 1.15 ? "favored" : ratio <= 0.87 ? "underdog" : "even";
    }
}
