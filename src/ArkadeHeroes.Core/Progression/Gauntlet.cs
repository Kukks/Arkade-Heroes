using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Content;
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
///
/// The LADDER ITSELF IS NOW AUTHORED DATA (<c>Content/dungeons.json</c>, dungeon id <c>gauntlet</c>):
/// wave count, per-wave level offsets and ghost gear, the XP schedule and its cap, the entry premium and
/// the drop table all come from <see cref="ContentPack.Default"/>. This type stays as the resolver every
/// caller already uses; only where the numbers come from moved. The treasury invariant below is therefore
/// no longer guarded by one test over one hand-written pool — <see cref="ContentValidation"/> enforces it
/// over ALL authored dungeons, at load time, and refuses to load content that breaks it.
/// </summary>
public static class Gauntlet
{
    /// <summary>The authored dungeon this resolver runs. Resolved once at type load; content that does not
    /// define it is a hard failure rather than a silently empty ladder.</summary>
    public static Dungeon Content { get; } = ContentPack.Default.FindDungeon("gauntlet")
        ?? throw new ContentValidationException(
            [new ContentError("missing-dungeon", "the content pack defines no dungeon 'gauntlet'")]);

    public static int WaveCount => Content.WaveCount;

    /// <summary>Above this level a run still costs the fee and can still win the item, but awards ZERO XP —
    /// the cap that makes the Sybil books close (buying XP below cost, then laundering it up the ladder, is
    /// killed by price + the transfer clamp).</summary>
    public static int PveXpLevelCap => Content.XpLevelCap;

    /// <summary>Per-wave clear XP (cumulative for a full clear = 130). Training-band only.</summary>
    public static IReadOnlyList<long> WaveXp => Content.Waves.Select(w => w.Xp).ToList();

    /// <summary>Flat premium over a same-level match fee — entry is strictly more expensive than the best
    /// item it can drop (500-sat tier), so PvE is EV-POSITIVE for the treasury (EV-negative for the PLAYER:
    /// a sats sink, not a faucet) at any clear rate. ContentValidation pins it for every authored dungeon,
    /// at every level, at load time.</summary>
    public static long FeeBonusSats => Content.EntryFeeBonusSats;

    /// <summary>The item pool a full clear can drop — all 500-sat tier, entropy-picked, each worth less than
    /// the entry fee. Public (like <see cref="WaveXp"/>) so the treasury-positive invariant can be pinned
    /// against the REAL pool instead of a copied number. Only lines that can actually be picked are listed.</summary>
    public static IReadOnlyList<string> RewardItems =>
        Content.Drops.Where(d => d.Weight > 0).Select(d => d.ItemId).ToList();

    /// <summary>Sats to enter: a same-level <see cref="Leveling.MatchFee"/> plus <see cref="FeeBonusSats"/>.</summary>
    public static long Fee(int heroLevel, GameConfig? config = null)
        => Leveling.MatchFee(heroLevel, config) + FeeBonusSats;

    /// <summary>The ghost's level for a wave: L-1, L, L+1, L+2, L+3 across waves 1..5 (floored at 1).</summary>
    public static int GhostLevel(int heroLevel, int wave) => Content.GhostLevel(heroLevel, wave);

    /// <summary>The ghost's equipment for a wave: waves 1-3 naked, wave 4 mid gear, wave 5 top gear.</summary>
    public static IReadOnlyList<string> GhostGear(int wave) => Content.GhostGear(wave);

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
    /// keeps its award, but future runs give nothing). Client and server must agree on the cap exactly,
    /// since the client's FairnessAudit recomputes this to check the awarded XP — which is why the content
    /// pack carries a stamped, resolvable version rather than being loaded on trust.</summary>
    public static long XpForRun(int preRunLevel, int wavesCleared)
    {
        if (preRunLevel >= PveXpLevelCap) return 0;
        return Content.XpFor(wavesCleared);
    }

    /// <summary>The item a run delivers — one entropy-picked 500-sat-tier item on a FULL clear, else none.</summary>
    public static string? RewardItem(ReadOnlySpan<byte> entropy, int wavesCleared)
        => Content.RollDrop(entropy, wavesCleared);
}
