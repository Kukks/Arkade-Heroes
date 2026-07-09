using ArkadeHeroes.Core;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Server;

/// <summary>
/// The append-only registry of published <see cref="GameConfig"/> versions. Version 0 is the
/// config projected from <see cref="GameOptions"/> at startup; retuning a PINNED value APPENDS a
/// new version (a HOT-only change mutates the current in place and does NOT append). Verification
/// resolves an artifact's stamped version here — <see cref="Get"/> — so an old artifact stays
/// verifiable under its own config forever, regardless of later retunes (the fix for the
/// mid-game-change break). In-memory for now: pre-launch, a restart reseeds version 0 and there is
/// no persisted corpus to strand; durable persistence (GameChainKv) is a launch follow-up.
/// </summary>
public sealed class GameConfigRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, GameConfig> _byVersion;
    private int _current;

    public GameConfigRegistry(IOptions<GameOptions> options)
        => _byVersion = new() { [0] = options.Value.ToGameConfig() };  // seed version 0

    /// <summary>The current (highest) published config — what new work is computed and stamped under.</summary>
    public GameConfig Current { get { lock (_gate) return _byVersion[_current]; } }

    /// <summary>The immutable config for a stamped version, or null if unknown.</summary>
    public GameConfig? Get(int version)
    {
        lock (_gate) return _byVersion.TryGetValue(version, out var c) ? c : null;
    }

    /// <summary>Appends a config as the next version (a PINNED retune). Returns it stamped with its assigned version.</summary>
    public GameConfig Register(GameConfig config)
    {
        lock (_gate)
        {
            var version = _current + 1;
            var stamped = config with { Version = version };
            _byVersion[version] = stamped;
            _current = version;
            return stamped;
        }
    }
}
