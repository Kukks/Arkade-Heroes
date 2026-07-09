using ArkadeHeroes.Core;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The payoff of the whole GameConfig pinning design: a PINNED value (here the leveling curve)
/// changing later must NOT retroactively alter facts made under the old version. ReplayLevel folds
/// each receipt under the curve of the version stamped on it, resolved from a per-version map.
/// </summary>
public class GameConfigPinningTests
{
    // ReplayLevel only reads Type/Id/HeroAId/HeroBId/ResultHeroId/XpAward*/UnixSeconds/ConfigVersion.
    private static ProgressionReceiptDto Match(string heroId, long award, int configVersion, string id, long t)
        => new("match", id, heroId, "opp", heroId, "seed", "nonce", "commit",
               award, 0, 1, 1, t, "key", "sig", configVersion);

    [Fact]
    public void ReplayLevelFoldsEachReceiptUnderItsOwnPinnedCurve()
    {
        // v0 = Default (XpToNext base 80). v1 = a MUCH steeper curve, so the same award barely levels.
        var v0 = GameConfig.Default;
        var v1 = v0 with { Version = 1, Curve = v0.Curve with { Base = 100_000 } };
        var byVersion = new Dictionary<int, GameConfig> { [0] = v0, [1] = v1 };

        var receipts = new[]
        {
            Match("H", award: 5_000, configVersion: 0, id: "m0", t: 1),  // earned under v0
            Match("H", award: 5_000, configVersion: 1, id: "m1", t: 2),  // earned under the steep v1
        };

        var pinned = ReceiptVerifier.ReplayLevel("H", receipts, byVersion); // each receipt under ITS curve
        var naive = ReceiptVerifier.ReplayLevel("H", receipts);            // null map → Default (v0) for BOTH

        // Folding the v1 receipt under its steep curve yields ~no levels, so per-version pinning lands
        // BELOW the naive all-v0 replay. That gap IS the fix: retuning the curve to v1 did not
        // re-level the v0-earned progression, and the v0 receipt still replays exactly as it always did.
        Assert.True(pinned < naive, $"pinned={pinned} must be < naive-all-v0={naive} (v1's steep curve gives fewer levels)");
        Assert.True(pinned >= 1);
    }

    [Fact]
    public void UnknownStampedVersionFallsBackToDefaultNeverThrows()
    {
        // A receipt stamped a version absent from the map resolves to Default rather than throwing.
        var receipts = new[] { Match("H", award: 5_000, configVersion: 99, id: "m", t: 1) };
        var withEmptyMap = ReceiptVerifier.ReplayLevel("H", receipts, new Dictionary<int, GameConfig>());
        var withNull = ReceiptVerifier.ReplayLevel("H", receipts);
        Assert.Equal(withNull, withEmptyMap); // both fold under Default
    }
}
