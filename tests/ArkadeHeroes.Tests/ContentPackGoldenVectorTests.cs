using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The ZERO-BEHAVIOUR-CHANGE ratchet for moving gear and dungeons out of C# literals and into authored
/// JSON. Gear stats feed combat resolution and combat is client-verifiable, so if the content pack loaded
/// even one stat differently from the catalog it replaced, an honest replay of an older match would
/// disagree with the server and the arena would render the literal string "SERVER CHEATED".
///
/// Each expected hash below was computed on this repo as it stood BEFORE the content pack existed (base
/// commit 149448d, with ItemCatalog still a hand-written C# list and Gauntlet still holding its own drop
/// pool). They are therefore evidence ABOUT the migration rather than a blessing of whatever the new
/// loader happens to emit — the same discipline
/// <see cref="GearCounterTests.FlagOffCombatMatchesItsPreFeatureGoldenVector"/> records for PR #155.
///
/// They stay as a RATCHET. Authoring is add-only, so a published item's stats must never move; any future
/// edit that changes one of these lines has to change it deliberately and say why.
/// </summary>
public class ContentPackGoldenVectorTests
{
    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Every FIELD of every item that existed when gear became authored data, in catalog order. This is the
    /// sharpest possible statement of "the JSON says exactly what the C# list said": a single transposed
    /// stat, a dropped MinLevel gate, a reordered list, or a counter that decoded to the wrong enum member
    /// all move it.
    ///
    /// It walks a FIXED list of ids rather than the whole catalog, on purpose. Authoring is add-only, and
    /// the point of this rung is that publishing a new item needs no code change — so a 14th item must not
    /// fail this test, while restatting any of these 13 must. Per-item immutability is enforced separately
    /// and generally by the seal ledger (<c>ContentValidationTests.RepricingAnAlreadyPublishedItemIsRefused</c>).
    /// </summary>
    [Fact]
    public void TheShippedGearCatalogMatchesItsPreContentPackGoldenVector()
    {
        var log = new StringBuilder();
        foreach (var id in MigratedItemIds)
        {
            var i = ItemCatalog.Find(id);
            Assert.True(i is not null, $"item '{id}' was published before the content pack and has vanished");
            log.Append(i!.Id).Append('|').Append(i.Name).Append('|').Append(i.Slot)
               .Append('|').Append(Int(i.Mods.MaxHp)).Append(',').Append(Int(i.Mods.Attack))
               .Append(',').Append(Int(i.Mods.Magic)).Append(',').Append(Int(i.Mods.Defense))
               .Append(',').Append(Int(i.Mods.Speed)).Append(',').Append(Int(i.Mods.CritPercent))
               .Append('|').Append(Int(i.PriceSats)).Append('|').Append(Int(i.MinLevel))
               .Append('|').Append(i.Counters?.ToString() ?? "-")
               .Append('|').Append(Int(i.VarianceBonus))
               .Append('\n');
        }

        Assert.Equal("2217d75141c40c4be1d347cf5ef1671e62a4bc5c2f32c661c207c24ab70e6ad1", Hash(log.ToString()));
    }

    /// <summary>The catalog exactly as it stood at base commit 149448d, in its authored order.</summary>
    private static readonly string[] MigratedItemIds =
    [
        "rusty-blade", "steel-saber", "arkforged-edge",
        "padded-vest", "chain-hauberk", "covenant-plate",
        "lucky-feather", "swift-anklet", "vtxo-charm",
        "bulwark-ward", "sunder-sigil", "snare-loop",
        "chaos-prism",
    ];

    /// <summary>
    /// The gauntlet's drop pick, EXHAUSTIVELY over the byte it reads. <c>RewardItem</c> selects with
    /// <c>entropy[0] % pool.Length</c>, so sweeping all 256 values of that byte pins the entire selection
    /// function — not a sample of it. A reordered pool, a resized pool, or a switch to a different roll
    /// would all show up here, and every one of them is a silent change to what players are paid.
    /// </summary>
    [Fact]
    public void TheGauntletDropPickMatchesItsPreContentPackGoldenVector()
    {
        var log = new StringBuilder();
        for (var b = 0; b < 256; b++)
        {
            var entropy = new byte[32];
            entropy[0] = (byte)b;
            // Both sides of the full-clear gate: a short run drops nothing, a full clear drops one item.
            for (var cleared = 0; cleared <= Gauntlet.WaveCount; cleared++)
                log.Append(Int(b)).Append(':').Append(Int(cleared)).Append('=')
                   .Append(Gauntlet.RewardItem(entropy, cleared) ?? "-").Append('\n');
        }

        Assert.Equal("ba23f09e597097a4159cf833b12b339d4d06c8fc0c87c9a1bc5cfa93d17b706f", Hash(log.ToString()));
    }

    /// <summary>
    /// Full gauntlet RUNS — the ladder, the ghosts' levels and gear, every fight's event log, the capped XP
    /// and the awarded item. This is the end-to-end statement that routing the dungeon through authored
    /// data moved nothing a verifier recomputes: <c>FairnessAudit.VerifyGauntlet</c> replays exactly this.
    ///
    /// THIS VECTOR HAS BEEN DELIBERATELY RE-BASELINED TWICE, and this is the "say why" the ratchet above
    /// demands. Both entries stay: the ratchet is a history, not a running total.
    ///
    /// (1) THE LEVEL-1 OPENER (PR #219). The ladder authors wave 1 at the runner's level MINUS ONE, and for
    /// a level-1 hero that offset used to evaporate against <c>Dungeon.GhostLevel</c>'s floor of 1 — so the
    /// entry cohort, and only the entry cohort, opened against a peer instead of the softer foe the content
    /// asked for, and saw one fewer distinct rung than everybody else. The floor is correct (there is no
    /// level 0, and the stat curve refuses one), so the shortfall it eats is paid on the damage axis instead
    /// (<c>Dungeon.GhostHandicap</c>). Blast radius MEASURED rather than assumed: of the 120 runs below
    /// exactly 4 moved — i = 0, 30, 60 and 90 — and level 1 was the only hero level among them. Every run at
    /// level 2 through 30 hashed byte-for-byte as before, because an unclamped wave's handicap is exactly
    /// 1.0 and multiplying a damage roll by exactly 1.0 is exact.
    ///
    /// (2) THE GHOST'S GENOME GRADE. <c>Gauntlet.GhostFor</c> used to build every ghost from
    /// <c>Genome.NewGen0</c> — raw hash bytes — however weak the runner's own statline was, so a bought
    /// recruit (stat and growth genes capped at 63 of 255) cleared NOTHING 96-99% of the time at EVERY
    /// level against a full-range hero's ~44%. The ghost's genome is now drawn at the runner's own
    /// <c>Genome.StatGeneCeiling</c>.
    ///
    /// Blast radius MEASURED, and it is one run: of the 120 heroes below, exactly ONE grades below the top
    /// rung — i = 57, at level 28, ceiling 127 — and only its fight LOG moves; it cleared 0 waves before and
    /// clears 0 now, so no reward, XP or drop in this vector changes at all. The other 119 hash byte-for-byte
    /// as before, because <c>Genome.NewRecruit</c> at a ceiling of 255 is the identity on the hash bytes and
    /// is therefore exactly <c>Genome.NewGen0</c> (pinned by
    /// <see cref="GauntletGradeTests.MintingAtTheTopRungIsExactlyAGen0Mint"/>).
    ///
    /// The honest consequence of both changes together: a gauntlet receipt stamped by an older build will
    /// not re-verify against this one IF it was run by a level-1 hero, or by a hero whose ten stat and
    /// growth genes all sit below 128 — the latter being about one hero in a thousand among full-range
    /// genomes, and every recruit.
    /// </summary>
    [Fact]
    public void GauntletRunsMatchTheirPreContentPackGoldenVector()
    {
        var log = new StringBuilder();
        var fullClears = 0;
        var drops = new HashSet<string>();

        for (var i = 0; i < 120; i++)
        {
            var hero = new Hero
            {
                Id = "h", OwnerId = "p", Name = "h",
                Genome = Bred(i), Generation = 3, Level = 1 + i % 30,
            };
            // Rotate through the gear tiers so item stats genuinely feed the resolved fights.
            foreach (var id in Sets[i % Sets.Length]) hero.Equipment.Equip(ItemCatalog.Find(id)!);

            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"gauntlet-golden-{i}"));
            var run = Gauntlet.Resolve(hero, entropy);
            var xp = Gauntlet.XpForRun(hero.Level, run.WavesCleared);
            var item = Gauntlet.RewardItem(entropy, run.WavesCleared);
            if (run.WavesCleared == Gauntlet.WaveCount) { fullClears++; if (item is not null) drops.Add(item); }

            log.Append(Int(hero.Level)).Append('|').Append(Int(Gauntlet.Fee(hero.Level)))
               .Append('|').Append(Int(run.WavesCleared)).Append('|').Append(Int(xp))
               .Append('|').Append(item ?? "-").Append('|');
            foreach (var w in run.Waves)
            {
                log.Append(Int(w.Wave)).Append(',').Append(Int(w.GhostLevel)).Append(',').Append(w.Won ? '1' : '0')
                   .Append(',').Append(string.Join('+', Gauntlet.GhostGear(w.Wave)))
                   .Append(',').Append(w.Result.WinnerId).Append(',').Append(Int(w.Result.Turns))
                   .Append(',').Append(Int(w.Result.WinnerRemainingHp)).Append(',');
                foreach (var e in w.Result.Events)
                    log.Append(e.Turn).Append('/').Append(e.ActorId).Append('/').Append(e.TargetId)
                       .Append('/').Append(e.Kind).Append('/').Append(e.SkillId).Append('/').Append(e.Damage)
                       .Append('/').Append(e.Crit).Append('/').Append(e.Healed).Append('/')
                       .Append(e.TargetHpAfter).Append(';');
                log.Append('#');
            }
            log.Append('\n');
        }

        // The vector is only worth anything if it actually reached the paying path.
        Assert.True(fullClears >= 5, $"only {fullClears} full clears — the drop path is barely covered");
        Assert.True(drops.Count >= 2, $"only {drops.Count} distinct drops — the pool pick is barely covered");

        Assert.Equal("43a49c55bdeda975e0c16c820b9990652afc81589d021bf602781dccf256e2d8", Hash(log.ToString()));
    }

    private static readonly string[][] Sets =
    [
        [],
        ["rusty-blade", "padded-vest", "lucky-feather"],
        ["steel-saber", "chain-hauberk", "swift-anklet"],
        ["arkforged-edge", "covenant-plate", "vtxo-charm"],
    ];

    /// <summary>A bred gen-3 genome — gen-0 starters have their trait genes cleared, so a bred fixture is
    /// what makes these runs exercise the full stat surface.</summary>
    private static Genome Bred(int i)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"gauntlet-shape-{i}"));
        var a = new byte[Genome.Size];
        var b = new byte[Genome.Size];
        for (var k = 0; k < Genome.Size; k++) { a[k] = h[k]; b[k] = (byte)(h[(k + 11) % 32] ^ 0x3C); }
        return GeneMixer.Mix(new Genome(a), new Genome(b), SHA256.HashData(Encoding.UTF8.GetBytes($"gauntlet-mix-{i}")));
    }
}
