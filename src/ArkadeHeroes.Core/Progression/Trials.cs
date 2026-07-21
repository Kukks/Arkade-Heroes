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
/// The rotating weekly rule that reshapes the Trials ladder — the renewable goal that makes the score
/// chase worth coming back to. Pure per week, and PINNED onto each run when it opens, so a replay verifies
/// against the same affix even after the week rolls over (recomputing it from "now" at verify time would
/// fail runs resolved near a boundary).
/// </summary>
public enum TrialsAffix
{
    None = 0,        // the plain ladder — the un-affixed baseline
    Ironclad,        // ghosts come armed from wave 1, top-geared early
    Relentless,      // levels climb two a wave — a short, brutal ladder
    Featherweight,   // ghosts fight bare-handed at every depth
    Veteran,         // the ladder starts five levels in
}

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

    private static readonly string[] MidGear = ["steel-saber", "chain-hauberk"];
    private static readonly string[] TopGear = ["arkforged-edge", "covenant-plate", "vtxo-charm"];

    /// <summary>
    /// Affixes only bite from this wave on, so every hero gets the SAME fair opening whatever the week.
    /// Measured: without this, Veteran's +5 offset made wave 1 a level-6 ghost and zeroed ~80% of level-5
    /// heroes — one week in four would be dead for everyone below the training band, which is the opposite
    /// of what a cold-start solo ladder is for. The affix still defines the climb, just not the doorstep.
    /// </summary>
    private const int AffixFromWave = 3;

    /// <summary>The affixes in rotation order. <see cref="TrialsAffix.None"/> is the un-affixed baseline and
    /// never rotates in.</summary>
    private static readonly TrialsAffix[] Rotation =
        [TrialsAffix.Ironclad, TrialsAffix.Relentless, TrialsAffix.Featherweight, TrialsAffix.Veteran];

    /// <summary>Whole weeks since <see cref="Season.Epoch"/> (0-based; anything before the epoch is week 0) —
    /// the same fixed anchor the seasons use, so week numbering is stable across the deployment.</summary>
    public static long WeekNumber(DateTimeOffset now)
        => Math.Max(0, (long)Math.Floor((now - Season.Epoch).TotalDays / 7));

    /// <summary>The affix for a given week — a fixed rotation, so it's predictable AND independently
    /// verifiable (no server discretion over which week is easy).</summary>
    public static TrialsAffix AffixForWeek(long week) => Rotation[(int)(Math.Max(0, week) % Rotation.Length)];

    /// <summary>The affix in force at <paramref name="now"/>.</summary>
    public static TrialsAffix AffixFor(DateTimeOffset now) => AffixForWeek(WeekNumber(now));

    /// <summary>A one-line player-facing description — client and server render the same text.</summary>
    public static string AffixDescription(TrialsAffix affix) => affix switch
    {
        TrialsAffix.Ironclad => "Ironclad — the ghosts come armed, and top-geared from wave 4.",
        TrialsAffix.Relentless => "Relentless — the ladder climbs two levels a wave.",
        TrialsAffix.Featherweight => "Featherweight — the ghosts fight bare-handed, however deep you go.",
        TrialsAffix.Veteran => "Veteran — the ladder starts five levels in.",
        _ => "No affix — the plain ladder.",
    };

    /// <summary>The ghost's level for a wave on the ABSOLUTE ladder: wave N's ghost is level N. So a hero
    /// clears the early waves easily and the run gets competitive around its own level, then inevitably
    /// overwhelming — the score tracks the hero's realized power rather than being normalized away. The
    /// weekly affix can steepen the climb (Relentless) or start it higher up (Veteran).</summary>
    public static int GhostLevel(int wave, TrialsAffix affix = TrialsAffix.None) =>
        wave < AffixFromWave ? wave : affix switch
        {
            TrialsAffix.Relentless => wave * 2,
            TrialsAffix.Veteran => wave + 5,
            _ => wave,
        };

    /// <summary>The ghost's gear for a wave — bands ramp with depth: naked early, mid gear from wave 8, top
    /// gear from wave 15. Stacked on the climbing level so deep waves punish even a maxed hero. The weekly
    /// affix can arm them from the start (Ironclad) or strip them entirely (Featherweight).</summary>
    public static IReadOnlyList<string> GhostGear(int wave, TrialsAffix affix = TrialsAffix.None) =>
        wave < AffixFromWave ? [] : affix switch
        {
            TrialsAffix.Featherweight => [],
            TrialsAffix.Ironclad => wave >= 4 ? TopGear : MidGear,
            _ => wave >= 15 ? TopGear : wave >= 8 ? MidGear : [],
        };

    /// <summary>The deterministic ghost for a wave — a gen-0 hero derived entirely from the run entropy, so
    /// the client re-derives the same ladder and the server cannot substitute a softer foe.</summary>
    public static Hero GhostFor(ReadOnlySpan<byte> entropy, int wave, TrialsAffix affix = TrialsAffix.None)
    {
        var genome = Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "trials-wave", wave.ToString()));
        var ghost = new Hero
        {
            Id = $"trial-ghost-{wave}",
            OwnerId = "trials",
            Name = $"Trial Wave {wave}",
            Genome = genome,
            Level = GhostLevel(wave, affix),
        };
        foreach (var itemId in GhostGear(wave, affix))
            ghost.Equipment.Equip(ItemCatalog.Find(itemId)!);
        return ghost;
    }

    /// <summary>Resolve an endless run: sequential full-HP fights up the climbing ghost ladder, ending at
    /// the first loss or <see cref="MaxWaves"/>. Pure + deterministic in (hero, entropy, config, affix) —
    /// the server scores with it and the client replays it identically. <paramref name="affix"/> comes last
    /// so existing positional calls that pass a config keep binding correctly.</summary>
    public static TrialsRun Resolve(
        Hero hero, ReadOnlySpan<byte> entropy, GameConfig? config = null, TrialsAffix affix = TrialsAffix.None)
    {
        var cfg = config ?? GameConfig.Default;
        var waves = new List<TrialsWave>();
        var entropyArr = entropy.ToArray();
        var cleared = 0;
        for (var wave = 1; wave <= MaxWaves; wave++)
        {
            var ghost = GhostFor(entropyArr, wave, affix);
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
