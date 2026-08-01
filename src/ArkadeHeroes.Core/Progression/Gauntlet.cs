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
/// (deterministic heroes derived from the run entropy and the runner's own level and genome grade, so the
/// server can't pick soft foes), one full-HP fight per wave, ending at the first loss. Server-scored +
/// fully client-replayable.
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

    /// <summary>The ghost's damage multiplier for a wave — exactly 1.0 everywhere except the waves where
    /// the level floor ate part of the authored offset, which on today's ladder is a level-1 hero's wave 1.
    /// See <see cref="Dungeon.GhostHandicap"/> for why the opener has to be expressed this way.</summary>
    public static double GhostHandicap(int heroLevel, int wave) => Content.GhostHandicap(heroLevel, wave);

    /// <summary>
    /// The deterministic ghost for a wave — derived entirely from the run entropy (so the client re-derives
    /// the same opponents; the server cannot substitute a weaker foe), at the RUNNER'S OWN GENOME GRADE as
    /// well as its level.
    ///
    /// <para>Scaling only the level could never have carried the entry cohort. Every ghost used to be built
    /// from <see cref="Genome.NewGen0"/> — raw hash bytes, mean stat gene 127 — while a bought recruit's
    /// stat and growth genes are squashed into the bottom quarter of the byte, mean 31. That is a
    /// MULTIPLICATIVE deficit in <c>StatBlock</c>: base stats read <c>10 + gene/4</c>, and per-level gains
    /// read <c>1 + growthGene/64</c>, so a capped growth gene is locked to the minimum tier while its foe
    /// gains up to four times as much. Raising the ghost's level raises the gap with it, which is why the
    /// cohort paying a full breed fee cleared NOTHING 96-99% of the time at EVERY level, against ~44% for a
    /// full-range hero — measured over 8000 seeded runs per level.</para>
    ///
    /// <para>The grade is a pure function of the runner's own genome, which the verifier already rebuilds
    /// from the signed hero snapshot it replays against — so this adds no wire field, no persisted flag and
    /// no server discretion. The server does not choose the grade any more than it chooses the entropy, and
    /// deriving it from the genome rather than from an "is a recruit" bit is also what makes it reach the
    /// cohorts a flag would miss: a hero bred from two recruits inherits their capped statline and was
    /// locked out just as hard (95-99%, measured), while being no kind of recruit itself.</para>
    ///
    /// <para>Because <see cref="Genome.NewRecruit"/> at a ceiling of 255 is byte-for-byte
    /// <see cref="Genome.NewGen0"/>, every runner whose statline reaches the top of the byte faces EXACTLY
    /// the ghost this ladder always built — so this is not a difficulty cut for everyone, and stamped
    /// replays of those runs still verify.</para>
    ///
    /// <para>Only the gauntlet is graded, and only on the GENOME. Gauntlet ghosts are content with no market
    /// side: nothing about what a recruit is worth to another player moves, because
    /// <c>StarterPolicy.RecruitStatCap</c> — the inflation valve that keeps a repeatable purchase from being
    /// a lottery ticket — is untouched. Equipment is deliberately NOT part of the grade: it is not part of
    /// the genome, and grading on it would pay a runner for unequipping.</para>
    /// </summary>
    public static Hero GhostFor(ReadOnlySpan<byte> entropy, int wave, Hero runner)
    {
        // The same capped mint a recruit is drawn from, at the RUNNER's ceiling rather than the recruit
        // policy's — so the ghost's statline is graded like the hero it was built to fight.
        var genome = Genome.NewRecruit(
            CommitReveal.DeriveEntropy(entropy, "gauntlet-wave", wave.ToString()),
            runner.Genome.StatGeneCeiling);
        var ghost = new Hero
        {
            Id = $"ghost-{wave}",
            OwnerId = "gauntlet",
            Name = $"Gauntlet Wave {wave}",
            Genome = genome,
            Level = GhostLevel(runner.Level, wave),
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
            var ghost = GhostFor(entropyArr, wave, hero);
            var fightSeed = CommitReveal.DeriveEntropy(entropyArr, "gauntlet-fight", wave.ToString());
            // The ghost carries a handicap only where the level floor ate the authored offset — exactly 1.0
            // (a no-op) on every other wave and for every hero above the floor.
            var result = BattleEngine.Fight(hero, ghost, fightSeed, cfg,
                advantageB: Content.GhostHandicap(hero.Level, wave));
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
