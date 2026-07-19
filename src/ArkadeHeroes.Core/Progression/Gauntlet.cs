using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Progression;

/// <summary>One resolved wave of a gauntlet run.</summary>
public sealed record GauntletWave(int Wave, int GhostLevel, bool Won, BattleResult Result);

/// <summary>A resolved gauntlet run: how many of the <see cref="Gauntlet.WaveCount"/> waves were cleared,
/// and each wave's fight (for replay/verification).</summary>
public sealed record GauntletRun(int WavesCleared, IReadOnlyList<GauntletWave> Waves);

/// <summary>
/// F1 — the solo PvE gauntlet: a hero runs a fixed ladder of commit-reveal-seeded GHOST opponents
/// (deterministic gen-0 heroes derived from the run entropy, so the server can't pick soft foes), one
/// full-HP fight per wave, ending at the first loss. Server-scored + fully client-replayable.
///
/// The reward is deliberately anti-farmable: training-band XP that is fee-priced and HARD-CAPPED at
/// <see cref="PveXpLevelCap"/> — the ONLY XP mint in the game, bounded so it can never seed the ladder
/// past the early band, and firewalled from the top by the conserved <see cref="Leveling.XpTransfer"/>
/// clamp (which zeroes XP flow up the ladder). A full clear also delivers one cheap item, negative-EV
/// versus the entry fee, so the treasury can never leak. Gauntlet receipts are NOT "match" receipts, so
/// they carry no leaderboard weight.
/// </summary>
public static class Gauntlet
{
    public const int WaveCount = 5;

    /// <summary>Above this level a run still costs the fee and can still win the item, but awards ZERO XP —
    /// the cap that makes the Sybil books close (buying XP below cost, then laundering it up the ladder, is
    /// killed by price + the transfer clamp).</summary>
    public const int PveXpLevelCap = 10;

    /// <summary>Per-wave clear XP (cumulative for a full clear = 130). Training-band only.</summary>
    public static readonly int[] WaveXp = [15, 20, 25, 30, 40];

    /// <summary>Flat premium over a same-level match fee — entry is strictly more expensive than the best
    /// item it can drop (500-sat tier), so PvE is EV-negative for the treasury at any clear rate.</summary>
    public const long FeeBonusSats = 250;

    // The item pool a full clear can drop — all 500-sat tier, entropy-picked; EV < the entry fee.
    private static readonly string[] RewardItems = ["rusty-blade", "padded-vest", "lucky-feather"];

    /// <summary>Sats to enter: a same-level <see cref="Leveling.MatchFee"/> plus <see cref="FeeBonusSats"/>.</summary>
    public static long Fee(int heroLevel, GameConfig? config = null)
        => Leveling.MatchFee(heroLevel, config) + FeeBonusSats;

    /// <summary>The ghost's level for a wave: L-1, L, L+1, L+2, L+3 across waves 1..5 (floored at 1).</summary>
    public static int GhostLevel(int heroLevel, int wave) => Math.Max(1, heroLevel + wave - 2);

    /// <summary>The ghost's equipment for a wave: waves 1-3 naked, wave 4 mid gear, wave 5 top gear.</summary>
    public static IReadOnlyList<string> GhostGear(int wave) => wave switch
    {
        4 => ["steel-saber", "chain-hauberk"],
        5 => ["arkforged-edge", "covenant-plate", "vtxo-charm"],
        _ => [],
    };

    /// <summary>The deterministic ghost for a wave — a gen-0 hero derived entirely from the run entropy
    /// (so the client re-derives the same opponents; the server cannot substitute a weaker foe).</summary>
    public static Hero GhostFor(ReadOnlySpan<byte> entropy, int wave, int heroLevel)
    {
        var genome = Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "gauntlet-wave", wave.ToString()));
        var ghost = new Hero
        {
            Id = $"ghost-{wave}",
            OwnerId = "gauntlet",
            Name = $"Gauntlet Wave {wave}",
            Genome = genome,
            Level = GhostLevel(heroLevel, wave),
        };
        foreach (var itemId in GhostGear(wave))
            ghost.Equipment.Equip(ItemCatalog.Find(itemId)!);
        return ghost;
    }

    /// <summary>Resolve a run: up to <see cref="WaveCount"/> sequential full-HP fights, ending at the first
    /// loss. Pure + deterministic in (hero, entropy, config) — the server scores with it and the client
    /// replays it identically.</summary>
    public static GauntletRun Resolve(Hero hero, ReadOnlySpan<byte> entropy, GameConfig? config = null)
    {
        var cfg = config ?? GameConfig.Default;
        var waves = new List<GauntletWave>();
        var entropyArr = entropy.ToArray();
        var cleared = 0;
        for (var wave = 1; wave <= WaveCount; wave++)
        {
            var ghost = GhostFor(entropyArr, wave, hero.Level);
            var fightSeed = CommitReveal.DeriveEntropy(entropyArr, "gauntlet-fight", wave.ToString());
            var result = BattleEngine.Fight(hero, ghost, fightSeed, cfg);
            var won = result.WinnerId == hero.Id;
            waves.Add(new GauntletWave(wave, ghost.Level, won, result));
            if (!won) break;
            cleared++;
        }
        return new GauntletRun(cleared, waves);
    }

    /// <summary>The XP a run awards: the cumulative wave-clear schedule, or ZERO if the hero was already at
    /// or past <see cref="PveXpLevelCap"/> before the run (the anti-farming cap; a run that crosses the cap
    /// keeps its award, but future runs give nothing). The cap is a compile-time const — client and server
    /// must agree exactly, since the client's FairnessAudit recomputes this to check the awarded XP.</summary>
    public static long XpForRun(int preRunLevel, int wavesCleared)
    {
        if (preRunLevel >= PveXpLevelCap) return 0;
        long xp = 0;
        for (var i = 0; i < wavesCleared && i < WaveXp.Length; i++) xp += WaveXp[i];
        return xp;
    }

    /// <summary>The item a run delivers — one entropy-picked 500-sat-tier item on a FULL clear, else none.</summary>
    public static string? RewardItem(ReadOnlySpan<byte> entropy, int wavesCleared)
        => wavesCleared >= WaveCount ? RewardItems[entropy[0] % RewardItems.Length] : null;
}
