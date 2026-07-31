using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;

namespace ArkadeHeroes.Core.Content;

/// <summary>
/// How a dungeon picks ONE drop out of its weighted table. Both modes are pure functions of the run's
/// commit-reveal entropy — there is deliberately no mode that reaches for ambient randomness, because a
/// drop a client cannot re-derive is a drop it cannot verify.
/// </summary>
public enum DropRoll
{
    /// <summary>
    /// The mode NEW content should author: <see cref="DeterministicRng"/> (xoshiro256**) seeded per run
    /// from <c>DeriveEntropy(entropy, "dungeon-drop", dungeonId)</c>, picking with rejection sampling so
    /// the table's weights are honoured without modulo bias. Domain-separated by dungeon id, so two
    /// dungeons resolved from the same run entropy do not roll in lockstep.
    /// </summary>
    DeterministicRng = 0,

    /// <summary>
    /// The PUBLISHED v1 gauntlet roll: the first entropy byte modulo the table's total weight. Kept
    /// because it is what shipped — the gauntlet's drops are already in players' hands and its runs are
    /// already replayable, so re-rolling them with a better sampler would silently change what an honest
    /// historical run paid out. It carries a slight modulo bias whenever 256 is not a multiple of the
    /// total weight (it is not biased at the gauntlet's own total of 3 in any way that matters, but it
    /// would be at, say, 7). Do not author it for anything new.
    /// </summary>
    EntropyByte = 1,
}

/// <summary>One weighted line of a dungeon's drop table. <paramref name="Weight"/> is a non-negative
/// INTEGER, never a percentage: the pick is then pure integer arithmetic, so it cannot pick up a rounding
/// or locale question on the way to a player's wallet. The authored chance of this line is
/// <c>Weight / (sum of weights)</c>.</summary>
public sealed record DungeonDrop(string ItemId, int Weight);

/// <summary>
/// One authored wave of a dungeon: the ghost's level RELATIVE to the running hero, the XP this wave pays
/// when cleared, and the gear the ghost brings.
/// </summary>
public sealed record DungeonWave(int Wave, int LevelOffset, long Xp, IReadOnlyList<string> GhostGear);

/// <summary>
/// An authored dungeon. Everything the resolver reads about a PvE ladder lives here rather than in C#, so
/// a new dungeon is a JSON edit — which is exactly why <see cref="ContentValidation"/> exists: a typo in a
/// drop table pays out real bitcoin.
/// </summary>
public sealed record Dungeon(
    string Id,
    string Name,
    long EntryFeeBonusSats,
    int XpLevelCap,
    bool DropRequiresFullClear,
    DropRoll Roll,
    IReadOnlyList<DungeonWave> Waves,
    IReadOnlyList<DungeonDrop> Drops)
{
    /// <summary>How many waves the ladder is — derived from the authored waves, never a second number that
    /// could disagree with them.</summary>
    public int WaveCount => Waves.Count;

    /// <summary>The total weight of the drop table; 0 means this dungeon drops nothing.</summary>
    public int TotalDropWeight => Drops.Sum(d => d.Weight);

    /// <summary>What one floor-eaten level is worth as a cut to the ghost's damage.
    ///
    /// Deliberately bigger than a level's own share of the stat curve (~6-8%): a level moves a ghost's HP,
    /// defense, speed and crit as well as its damage, so buying the same difference on the damage axis
    /// ALONE costs more. Calibrated against the ladder's measured outcome rather than derived — at this
    /// value a level-1 hero's rate of clearing NOTHING lands inside the band its neighbouring levels
    /// already sit in, which is exactly the intent: the entry cohort joins the ladder, it is not handed an
    /// easier one.</summary>
    public const double GhostHandicapPerClampedLevel = 0.15;

    /// <summary>Floor on <see cref="GhostHandicap"/>. Nothing in the shipped ladder comes near it — the
    /// deepest clamp today is a single level — but waves are AUTHORED, and an offset of -7 would otherwise
    /// walk the multiplier down to zero and out the other side into negative damage.</summary>
    public const double MinGhostHandicap = 0.25;

    /// <summary>The level the CONTENT asks a wave's ghost to be — hero level plus authored offset, before
    /// the floor has its say. Not public: it can be zero or negative, which is a question about authoring
    /// intent rather than a level anything may be resolved at. <see cref="GhostLevel"/> and
    /// <see cref="ClampedLevels"/> are the two halves of the answer, and they read it from here rather than
    /// each spelling the floor out, so the two can never drift apart on where it sits.</summary>
    private int AuthoredGhostLevel(int heroLevel, int wave) => heroLevel + (WaveAt(wave)?.LevelOffset ?? 0);

    /// <summary>The ghost's level for a wave: the hero's level plus the wave's authored offset, floored at
    /// 1. An unknown wave floors to 1 rather than throwing — the ladder is bounded by
    /// <see cref="WaveCount"/> and every caller iterates it.</summary>
    public int GhostLevel(int heroLevel, int wave) => Math.Max(1, AuthoredGhostLevel(heroLevel, wave));

    /// <summary>
    /// How many levels of a wave's authored offset the level-1 FLOOR ate — 0 for every hero the offset can
    /// actually reach.
    ///
    /// A ladder authored as "one level below the runner" has nowhere to land on a level-1 hero, because
    /// there is no level 0. The floor in <see cref="GhostLevel"/> is right on its own terms — a hero below
    /// level 1 is not a thing this game has — but silently dropping the authored intent is not. It left the
    /// ENTRY cohort, and only the entry cohort, without the soft opener every other cohort is given, and
    /// with one fewer distinct rung than the ladder was written to have. This is that shortfall, measured,
    /// so <see cref="GhostHandicap"/> can pay it back on an axis that still has room below a level-1 peer.
    ///
    /// By construction <c>GhostLevel == AuthoredGhostLevel + ClampedLevels</c> for every input: what the
    /// floor added is exactly what this reports, so nothing the content asked for goes unaccounted.
    /// </summary>
    public int ClampedLevels(int heroLevel, int wave) => Math.Max(0, 1 - AuthoredGhostLevel(heroLevel, wave));

    /// <summary>
    /// How hard a wave's ghost hits, as a multiplier on its damage — exactly 1.0 unless the level floor ate
    /// part of the authored offset (<see cref="ClampedLevels"/>).
    ///
    /// This is the only axis on which "easier than a level-1 peer" can be SAID. It cannot be said by
    /// retuning the authored ladder, because <c>max(1, 1 + offset)</c> is 1 for every offset at or below 0
    /// — no level offset reaches under the floor. And it must not be said by dropping the floor to 0: the
    /// stat curve refuses a level below 1 outright, and a level-0 ghost would also fall under the first
    /// gene-skill unlock, so it would arrive a whole SKILL short rather than one level weaker — a cliff,
    /// not a rung.
    ///
    /// So the shortfall is paid on the damage axis the engine already has: the same per-side multiplier
    /// squad synergy rides (<c>BattleEngine.Fight</c>'s <c>advantageB</c>). That adds no draw to the RNG
    /// stream and no field to the authored schema, and because an unclamped wave returns exactly 1.0 — and
    /// multiplying a double by exactly 1.0 is exact — every cohort the floor never touched resolves
    /// BIT-FOR-BIT as it did before this existed.
    ///
    /// LINEAR and floored rather than compounded, so only IEEE-754 basic operations appear here and the
    /// value is bit-identical on the server and in the browser that re-derives it. <c>Math.Pow</c> is not
    /// guaranteed to agree across platforms, and a replay that disagrees renders "SERVER CHEATED".
    /// </summary>
    public double GhostHandicap(int heroLevel, int wave) =>
        Math.Max(MinGhostHandicap, 1.0 - GhostHandicapPerClampedLevel * ClampedLevels(heroLevel, wave));

    /// <summary>The gear the wave's ghost brings, or empty.</summary>
    public IReadOnlyList<string> GhostGear(int wave) => WaveAt(wave)?.GhostGear ?? [];

    /// <summary>The cumulative XP for clearing <paramref name="wavesCleared"/> waves, in authored order.</summary>
    public long XpFor(int wavesCleared)
    {
        long xp = 0;
        for (var i = 0; i < wavesCleared && i < Waves.Count; i++) xp += Waves[i].Xp;
        return xp;
    }

    /// <summary>
    /// The item this run drops, or null. Deterministic in the run entropy under either
    /// <see cref="DropRoll"/> mode, so <c>FairnessAudit</c> recomputes it exactly and a server cannot
    /// quietly pay a different item than the one the entropy names.
    /// </summary>
    public string? RollDrop(ReadOnlySpan<byte> entropy, int wavesCleared)
    {
        if (wavesCleared <= 0) return null;
        if (DropRequiresFullClear && wavesCleared < WaveCount) return null;

        var total = TotalDropWeight;
        if (total <= 0) return null;

        var pick = Roll switch
        {
            // The published v1 roll. With the gauntlet's three weight-1 lines this is byte-for-byte the
            // `RewardItems[entropy[0] % 3]` the gauntlet has always computed.
            DropRoll.EntropyByte => entropy[0] % total,
            _ => new DeterministicRng(CommitReveal.DeriveEntropy(entropy, "dungeon-drop", Id)).Next(total),
        };

        var cumulative = 0;
        foreach (var drop in Drops)
        {
            cumulative += drop.Weight;
            if (pick < cumulative) return drop.ItemId;
        }
        // Unreachable while total > 0: the walk always covers [0, total).
        return Drops[^1].ItemId;
    }

    private DungeonWave? WaveAt(int wave) => Waves.FirstOrDefault(w => w.Wave == wave);
}

/// <summary>
/// The authored CONTENT the game resolves under — gear and dungeons — as data rather than C# literals, so
/// the owner can release an item or a dungeon without a code change.
///
/// Gear stats feed combat resolution and combat is CLIENT-VERIFIABLE, so this pack is part of the
/// verification surface exactly as <see cref="GameConfig"/> is: it carries a deterministic
/// <see cref="ContentPackVersion"/>, that version is stamped on every outcome resolved under it, and a
/// client resolves an unfamiliar stamp through <c>GET /api/content/{version}</c> rather than assuming its
/// own compiled-in pack. Skipping that would mean an honest replay of an older match silently disagreeing
/// with the server and the arena rendering the literal string "SERVER CHEATED".
///
/// Authoring is ADD-ONLY. A published item id is immutable: a "change" is a NEW id. Two players holding
/// the same item id must never have different stats, and <see cref="ContentValidation"/> enforces it.
/// </summary>
public sealed record ContentPack(
    string PackId,
    IReadOnlyList<Item> Items,
    IReadOnlyList<Dungeon> Dungeons,
    /// <summary>The authored bytes this pack was parsed from, kept so a server can serve a version it
    /// stamped back to a verifier VERBATIM (see <c>ContentPackDto</c>). Deliberately NOT part of
    /// <see cref="ContentPackVersion"/>: reformatting a file must not mint a new version, because the
    /// content it describes has not changed. Empty for a pack built in memory.</summary>
    string ItemsJson = "",
    string DungeonsJson = "")
{
    /// <summary>The pack compiled into this build, parsed and VALIDATED from the embedded JSON. A pack
    /// that fails validation throws here rather than loading — bad content must not resolve a single
    /// match, so the failure is a startup crash, not a warning.</summary>
    public static ContentPack Default { get; } = ContentPackLoader.LoadEmbedded();

    /// <summary>This pack's version id: 64 lowercase hex chars. See <see cref="ContentPackVersion"/>.
    /// Deliberately NOT cached in a field: a record's <c>with</c> copies every backing field, so a cached
    /// id would survive onto a modified pack and name rules that pack does not have. Callers on a hot path
    /// (the server stamps every outcome) cache it themselves against their own immutable instance.</summary>
    public string Version => ContentPackVersion.Compute(this);

    public Item? FindItem(string id) => Items.FirstOrDefault(i => i.Id == id);

    public Dungeon? FindDungeon(string id) => Dungeons.FirstOrDefault(d => d.Id == id);
}
