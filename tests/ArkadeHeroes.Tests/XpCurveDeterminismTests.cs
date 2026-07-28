using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// CROSS-PLATFORM stability of the XP curve. <see cref="Leveling.XpToNext"/> is
/// <c>Base + (long)(Coefficient · level^Exponent)</c>, and a hero's level is recomputed independently by the
/// client — <c>ReceiptVerifier.ReplayLevel</c> folds the signed receipts through <see cref="Leveling.Apply"/>,
/// in the console client and in the BROWSER (Blazor WASM, a different runtime and a different libm than the
/// server's). <see cref="Math.Pow"/> with a non-integral exponent is NOT correctly-rounded and is explicitly
/// allowed to differ between platforms, and the result is TRUNCATED to a long — so a threshold sitting a hair
/// under an integer could truncate one lower on one platform than the other. A single hero sitting exactly on
/// that boundary would then replay to a different level in the browser than the server reports, and an honest
/// player's genuine receipt chain would read as tampered. Same family as the culture-sensitive signed preimage
/// and the endian-dependent shuffle read: ambient machine state leaking into something both sides must agree on.
///
/// The shipped curve is safe by a wide margin — this pins that. The numbers are a tunable
/// <see cref="XpCurve"/>, so a future rebalance is exactly the change that could quietly land a threshold on a
/// boundary; these fail loudly if it ever does.
/// </summary>
public class XpCurveDeterminismTests
{
    // Comfortably wider than any real divergence, comfortably tighter than the shipped curve's own margin.
    // Two independent libm implementations of pow differ by a few ULP — around 1e-16 relative. The shipped
    // curve's tightest approach to a boundary is ~2.2e-6 relative (level 33). This threshold sits roughly
    // ten million times above the former and two thousand times below the latter, so it catches a genuinely
    // dangerous curve without tripping on a merely-unlucky-looking one.
    private const double MinRelativeMargin = 1e-9;

    // Level 1 is excluded from the numeric sweeps and pinned separately below: pow(1, y) is exactly 1 on every
    // conforming platform, so it cannot diverge, and its threshold lands exactly on an integer by construction.
    private static IEnumerable<int> TranscendentalLevels(XpCurve c) =>
        Enumerable.Range(2, c.MaxLevel - 1);

    /// <summary>How close Coefficient·level^Exponent sits to an integer, relative to its own magnitude.</summary>
    private static double RelativeMarginToBoundary(XpCurve c, int level)
    {
        var raw = c.Coefficient * Math.Pow(level, c.Exponent);
        var margin = Math.Min(raw - Math.Floor(raw), Math.Ceiling(raw) - raw);
        return raw == 0 ? 0 : margin / raw;
    }

    [Fact]
    public void EveryThresholdSitsClearOfATruncationBoundary()
    {
        var c = GameConfig.Default.Curve;
        foreach (var level in TranscendentalLevels(c))
        {
            var margin = RelativeMarginToBoundary(c, level);
            Assert.True(margin > MinRelativeMargin,
                $"level {level}: Coefficient·{level}^Exponent = {c.Coefficient * Math.Pow(level, c.Exponent):R} " +
                $"sits {margin:E3} (relative) from an integer, inside the {MinRelativeMargin:E0} safety margin — " +
                "a platform whose Math.Pow rounds the other way would truncate this threshold one lower, so the " +
                "server and a browser client would disagree about when this level is reached");
        }
    }

    [Fact]
    public void ThresholdsAreUnchangedByACrossPlatformPowPerturbation()
    {
        // The threat, modelled directly rather than by proxy: nudge the pow result by a few ULP in BOTH
        // directions — standing in for another runtime's libm landing on a neighbouring double — and require
        // the truncated long to come out identical. This is the property the client's replay actually needs.
        var c = GameConfig.Default.Curve;
        foreach (var level in TranscendentalLevels(c))
        {
            var raw = c.Coefficient * Math.Pow(level, c.Exponent);
            var expected = c.Base + (long)raw;

            var low = raw;
            var high = raw;
            for (var i = 0; i < 4; i++)
            {
                low = Math.BitDecrement(low);
                high = Math.BitIncrement(high);
            }

            Assert.True(expected == c.Base + (long)low && expected == c.Base + (long)high,
                $"level {level}: XpToNext is {expected} but shifts to {c.Base + (long)low}/{c.Base + (long)high} " +
                "under a 4-ULP nudge — this threshold is not stable across runtimes");
        }
    }

    [Fact]
    public void TheShippedCurveMatchesTheRealHelper()
    {
        // Keeps the sweeps above honest: they reason about Coefficient·level^Exponent directly, so pin that this
        // is genuinely what XpToNext computes. If the helper's formula ever changes, these guards must follow.
        var c = GameConfig.Default.Curve;
        foreach (var level in TranscendentalLevels(c))
            Assert.Equal(c.Base + (long)(c.Coefficient * Math.Pow(level, c.Exponent)), Leveling.XpToNext(level));
    }

    [Fact]
    public void LevelOneIsExactAndCannotDiverge()
    {
        // Why level 1 is left out above. IEEE 754 requires pow(1, y) == 1 for every y, so this threshold is
        // exactly Base + Coefficient on any conforming runtime — landing on an integer here is safe, not risky.
        var c = GameConfig.Default.Curve;
        Assert.Equal(1.0, Math.Pow(1, c.Exponent));
        Assert.Equal(c.Base + (long)c.Coefficient, Leveling.XpToNext(1));
    }

    [Fact]
    public void TheBoundaryGuardHasTeeth()
    {
        // Proves the margin check is load-bearing rather than vacuously true of any curve. This coefficient is
        // chosen so the level-2 threshold lands exactly on 100 — the shape a rebalance could stumble into — and
        // the SAME predicate the shipped curve passes must reject it.
        var dangerous = GameConfig.Default.Curve with { Coefficient = 100.0 / Math.Pow(2, 1.35) };

        var margin = RelativeMarginToBoundary(dangerous, 2);
        Assert.True(margin <= MinRelativeMargin,
            $"expected the constructed on-boundary curve to be caught, but level 2 reported a margin of {margin:E3}");

        // And it is genuinely ambiguous: one ULP either side of the boundary truncates to different integers.
        var raw = dangerous.Coefficient * Math.Pow(2, dangerous.Exponent);
        Assert.NotEqual((long)Math.BitDecrement(raw), (long)Math.BitIncrement(raw));
    }
}
