using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Progression;

/// <summary>One resolved wave of a trials run.</summary>
public sealed record TrialsWave(int Wave, int GhostLevel, bool Won, BattleResult Result);

/// <summary>A resolved endless-trials run: how many waves the hero survived (the score) and each wave's
/// fight (for replay/verification).</summary>
public sealed record TrialsRun(int WavesCleared, IReadOnlyList<TrialsWave> Waves);

/// <summary>
/// The endless solo Trials — a leaderboard-focused PvE ladder that needs no live opponent (cold-start
/// insurance for a young game). A hero fights an endless run of commit-reveal-seeded GHOST opponents on
/// an ABSOLUTE difficulty ladder (wave N's ghost is level N), one full-HP fight per wave, ending at the
/// first loss. The score is how many waves you survived — because the ladder is absolute, that score is a
/// direct read of the hero's realized power (level + gear + traits), so it makes a meaningful leaderboard
/// and always terminates (the ghost inevitably outlevels any hero). Server-scored + fully client-replayable
/// (deterministic in hero, entropy, config).
///
/// Unlike <see cref="Gauntlet"/> (the fee-gated, level-capped XP faucet), Trials awards NO XP, item, or
/// sats — only a score and a flavor <see cref="TitleFor"/>. That keeps it EV-neutral for the treasury
/// (nothing to farm), so it can be free/cheap and replayed as often as a player likes.
/// </summary>
public static class Trials
{
    /// <summary>Hard ceiling on waves — a compute safety net so a maxed hero can't loop forever against the
    /// ramping ladder (the score caps here). Set above the hero level ceiling so it never truncates a real
    /// run before the ghost outclasses the hero.</summary>
    public const int MaxWaves = 60;

    /// <summary>The ghost's level for a wave on the ABSOLUTE ladder: wave N's ghost is level N. So a hero
    /// clears the early waves easily and the run gets competitive around its own level, then inevitably
    /// overwhelming — the score tracks the hero's realized power rather than being normalized away.</summary>
    public static int GhostLevel(int wave) => wave;

    /// <summary>The ghost's gear for a wave — bands ramp with depth: naked early, mid gear from wave 8, top
    /// gear from wave 15. Stacked on the climbing level so deep waves punish even a maxed hero.</summary>
    public static IReadOnlyList<string> GhostGear(int wave) => wave switch
    {
        >= 15 => ["arkforged-edge", "covenant-plate", "vtxo-charm"],
        >= 8 => ["steel-saber", "chain-hauberk"],
        _ => [],
    };

    /// <summary>The deterministic ghost for a wave — a gen-0 hero derived entirely from the run entropy, so
    /// the client re-derives the same ladder and the server cannot substitute a softer foe.</summary>
    public static Hero GhostFor(ReadOnlySpan<byte> entropy, int wave)
    {
        var genome = Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "trials-wave", wave.ToString()));
        var ghost = new Hero
        {
            Id = $"trial-ghost-{wave}",
            OwnerId = "trials",
            Name = $"Trial Wave {wave}",
            Genome = genome,
            Level = GhostLevel(wave),
        };
        foreach (var itemId in GhostGear(wave))
            ghost.Equipment.Equip(ItemCatalog.Find(itemId)!);
        return ghost;
    }

    /// <summary>Resolve an endless run: sequential full-HP fights up the climbing ghost ladder, ending at
    /// the first loss or <see cref="MaxWaves"/>. Pure + deterministic in (hero, entropy, config) — the
    /// server scores with it and the client replays it identically.</summary>
    public static TrialsRun Resolve(Hero hero, ReadOnlySpan<byte> entropy, GameConfig? config = null)
    {
        var cfg = config ?? GameConfig.Default;
        var waves = new List<TrialsWave>();
        var entropyArr = entropy.ToArray();
        var cleared = 0;
        for (var wave = 1; wave <= MaxWaves; wave++)
        {
            var ghost = GhostFor(entropyArr, wave);
            var fightSeed = CommitReveal.DeriveEntropy(entropyArr, "trials-fight", wave.ToString());
            var result = BattleEngine.Fight(hero, ghost, fightSeed, cfg);
            var won = result.WinnerId == hero.Id;
            waves.Add(new TrialsWave(wave, ghost.Level, won, result));
            if (!won) break;
            cleared++;
        }
        return new TrialsRun(cleared, waves);
    }

    /// <summary>A flavor title earned for a run's depth — the only "reward" (no XP/item/sats). Client-
    /// verifiable, so it recomputes exactly. Null below the first band.</summary>
    public static string? TitleFor(int wavesCleared) => wavesCleared switch
    {
        >= 20 => "Trial Legend",
        >= 12 => "Trialblazer",
        >= 6 => "Trialgoer",
        _ => null,
    };
}
