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
}
