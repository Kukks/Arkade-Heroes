using System.Collections.Concurrent;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Server;

public class Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public bool StarterClaimed { get; set; }
}

public class BreedingSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string ParentAId { get; init; }
    public required string ParentBId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? ChildHeroId { get; set; }
}

public class MatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required string ChallengerHeroId { get; init; }
    public required string DefenderHeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stake each side escrows with the treasury; 0 = friendly match.</summary>
    public long WagerSats { get; init; }

    /// <summary>Owner of the defender hero at open time (must accept wagered matches).</summary>
    public string? DefenderPlayerId { get; set; }

    public string Status { get; set; } = "open"; // open | accepted | resolved
    public BattleResult? Result { get; set; }
    public string? EntropyHex { get; set; }
    public string? Nonce { get; set; }
}

/// <summary>In-process game state. v1 keeps everything in memory; the chain is the durable layer for heroes.</summary>
public class GameStore
{
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Player> PlayersByToken { get; } = new();
    public ConcurrentDictionary<string, Hero> Heroes { get; } = new();
    public ConcurrentDictionary<string, BreedingSession> Breedings { get; } = new();
    public ConcurrentDictionary<string, MatchSession> Matches { get; } = new();
}
