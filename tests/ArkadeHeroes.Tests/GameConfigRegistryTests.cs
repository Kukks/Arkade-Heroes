using ArkadeHeroes.Core;
using ArkadeHeroes.Server;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Tests;

/// <summary>The version registry that makes PINNED config per-artifact resolvable (the mid-game-change fix).</summary>
public class GameConfigRegistryTests
{
    private static GameConfigRegistry NewRegistry(GameOptions? options = null)
        => new(Options.Create(options ?? new GameOptions()));

    [Fact]
    public void SeedsVersion0FromOptions()
    {
        var options = new GameOptions { MatchmakingTake = 7 };
        var registry = NewRegistry(options);

        Assert.Equal(0, registry.Current.Version);
        Assert.Equal(7, registry.Current.MatchmakingTake);          // projected from options
        Assert.Same(registry.Current, registry.Get(0));             // version 0 IS the current seed
    }

    [Fact]
    public void UnknownVersionResolvesToNull()
        => Assert.Null(NewRegistry().Get(999));

    [Fact]
    public void RegisterAppendsNextVersionStampedAndImmutable()
    {
        var registry = NewRegistry();
        var seed = registry.Current;                                // version 0

        var v1 = registry.Register(seed with { MatchmakingTake = 42 });

        Assert.Equal(1, v1.Version);                                // assigned the next version
        Assert.Equal(42, v1.MatchmakingTake);
        Assert.Same(v1, registry.Get(1));                           // resolvable by its version
        Assert.Equal(1, registry.Current.Version);                  // current advanced to the append
        Assert.Equal(0, registry.Get(0)!.Version);                  // version 0 still resolves to the old config
        Assert.NotEqual(registry.Get(0)!.MatchmakingTake, v1.MatchmakingTake);
    }
}
