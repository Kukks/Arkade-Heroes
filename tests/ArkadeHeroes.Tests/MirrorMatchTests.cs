using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using System.Security.Cryptography;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A hero fighting itself. The engine used to refuse it outright, which was a rule with nothing behind it:
/// each side builds its own FighterState, so a mirror match shares no mutable state and the deterministic
/// RNG separates two identical fighters exactly as it separates twins.
/// </summary>
public class MirrorMatchTests
{
    /// <summary>DeterministicRng wants exactly 32 bytes.</summary>
    private static byte[] Seed(string s) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));

    private static Hero Twin() => new()
    {
        Id = "mirror", OwnerId = "p", Name = "Mirror",
        Genome = Genome.NewGen0("mirror-seed"u8.ToArray()), Level = 5,
    };

    /// <summary>It resolves, names one side, and terminates — no hang, no tie limbo.</summary>
    [Fact]
    public void AHeroCanFightItself_AndTheFightResolves()
    {
        var result = BattleEngine.Fight(Twin(), Twin(), Seed("mirror-match"));

        Assert.False(string.IsNullOrEmpty(result.WinnerId));
        Assert.InRange(result.Turns, 1, BattleEngine.MaxTurns);
        Assert.NotEmpty(result.Events);
    }

    /// <summary>
    /// Still deterministic, which is what makes a mirror match replayable like any other: the same seed
    /// must reproduce the same fight, or the verifiable-replay promise stops holding for this shape.
    /// </summary>
    [Fact]
    public void AMirrorMatch_IsReplayableFromItsSeed()
    {
        var seed = Seed("same-seed");
        var first = BattleEngine.Fight(Twin(), Twin(), seed);
        var again = BattleEngine.Fight(Twin(), Twin(), seed);

        Assert.Equal(first.WinnerId, again.WinnerId);
        Assert.Equal(first.Turns, again.Turns);
        Assert.Equal(first.WinnerRemainingHp, again.WinnerRemainingHp);
    }
}
