using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Combat;

/// <summary>
/// Eight-element ring: each element deals 1.3× to the next element on the ring
/// and 0.75× to the previous one; everything else is neutral.
/// </summary>
public static class ElementMatrix
{
    public const double Strong = 1.3;
    public const double Weak = 0.75;
    public const double Neutral = 1.0;

    public static double Multiplier(Element attacker, Element defender, GameConfig? config = null)
    {
        var c = (config ?? GameConfig.Default).Combat;
        var a = (int)attacker;
        var d = (int)defender;
        if ((a + 1) % 8 == d) return c.ElementStrong;
        if ((d + 1) % 8 == a) return c.ElementWeak;
        return Neutral;
    }
}
