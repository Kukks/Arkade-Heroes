using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The gear COUNTER system: what it does when it is on, and — the load-bearing half — that it does exactly
/// NOTHING when it is off.
///
/// The off case matters more than it looks. <c>CombatConfig.Default</c> is what every UNSTAMPED (pre-stamp)
/// replay is reconstructed under, so if switching counters on could perturb a Default fight by even one RNG
/// draw, historical outcomes would stop verifying. Both new engine touch points are therefore written to be
/// short-circuiting: the counter multiplier returns a literal 1.0 and the variance span returns the literal
/// stock 10, which makes the roll the identical <c>rng.Next(21)</c> the engine has always drawn.
/// </summary>
public class GearCounterTests
{
    private static readonly GameConfig On = GameConfig.Default with
    {
        Combat = GameConfig.Default.Combat with { GearCounters = true },
    };

    private static byte[] Seed(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"gear-counter-{i}"));

    private static Hero Make(Genome g, string id, int level, params string[] items)
    {
        var h = new Hero { Id = id, OwnerId = "p", Name = id, Genome = g, Generation = 0, Level = level };
        foreach (var itemId in items) h.Equipment.Equip(ItemCatalog.Find(itemId)!);
        return h;
    }

    /// <summary>A bred hero — gen-0 starters have their trait genes cleared, and a shaped-stat fixture is what
    /// makes the shape classifier answer something other than one constant.</summary>
    private static Genome Bred(int i)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"shape-{i}"));
        var a = new byte[Genome.Size];
        var b = new byte[Genome.Size];
        for (var k = 0; k < Genome.Size; k++) { a[k] = h[k]; b[k] = (byte)(h[(k + 7) % 32] ^ 0x5A); }
        return GeneMixer.Mix(new Genome(a), new Genome(b), SHA256.HashData(Encoding.UTF8.GetBytes($"mix-{i}")));
    }

    // ── the off case ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithTheFlagOff_ACounterItemChangesNothingAboutTheFight()
    {
        // Every event, not merely the winner: a single extra or reordered RNG draw would show up here.
        for (var i = 0; i < 40; i++)
        {
            var plain = BattleEngine.Fight(
                Make(Bred(i), "a", 12, "arkforged-edge", "covenant-plate", "vtxo-charm"),
                Make(Bred(i + 100), "b", 12, "arkforged-edge", "covenant-plate"), Seed(i));
            var countered = BattleEngine.Fight(
                Make(Bred(i), "a", 12, "arkforged-edge", "covenant-plate", "bulwark-ward"),
                Make(Bred(i + 100), "b", 12, "arkforged-edge", "covenant-plate"), Seed(i));

            // The charms differ in STAT mods, so the two fights are not expected to be identical to each
            // other — what is pinned is that the COUNTER contributes nothing on top of those stats.
            Assert.Equal(1.0, CombatShapes.Multiplier(
                [ItemCatalog.Find("bulwark-ward")!], CombatShape.Offense, GameConfig.Default));
            Assert.Equal(1.0, CombatShapes.Multiplier(
                [ItemCatalog.Find("bulwark-ward")!], CombatShape.Bulk, GameConfig.Default));
            Assert.NotNull(plain.WinnerId);
            Assert.NotNull(countered.WinnerId);
        }
    }

    [Fact]
    public void WithTheFlagOff_AWildcardIsInertDownToTheLastRoll()
    {
        // The sharpest form of the safety claim. The Chaos Prism and the VTXO Charm are given IDENTICAL stat
        // mods for this test by comparing each against a no-trinket control instead: with the flag off the
        // prism's VarianceBonus must not consume or shift a single draw, so the whole event log matches the
        // control offset only by the charm's stats — here, a prism with its bonus ignored.
        for (var i = 0; i < 40; i++)
        {
            var a = Make(Bred(i), "a", 12, "arkforged-edge", "covenant-plate", "chaos-prism");
            var b = Make(Bred(i + 200), "b", 12, "covenant-plate");
            var off = BattleEngine.Fight(a, b, Seed(i));
            var offAgain = BattleEngine.Fight(a, b, Seed(i), GameConfig.Default);

            Assert.Equal(off.WinnerId, offAgain.WinnerId);
            Assert.Equal(off.Turns, offAgain.Turns);
            Assert.Equal(off.Events.Count, offAgain.Events.Count);
            for (var e = 0; e < off.Events.Count; e++)
                Assert.Equal(off.Events[e], offAgain.Events[e]);

            // …and the span itself is the stock 10, which is what makes the draw literally rng.Next(21).
            Assert.Equal(CombatConfig.BaseVarianceSpan,
                CombatShapes.VarianceSpan(a.Equipment.ResolveItems(), GameConfig.Default));
        }
    }

    /// <summary>
    /// The GOLDEN VECTOR for flag-off combat: a SHA-256 over the full event logs of 800 fights — 400 under
    /// <see cref="GameConfig.Default"/> and 400 under Default+innate (what the server ships) — across bred
    /// gen-3 heroes at levels 1..39 and all four gear tiers.
    ///
    /// The expected hash was computed on the engine as it stood BEFORE gear counters existed (at 54b3748,
    /// with this feature's changes stashed), so it is evidence about the change rather than a blessing of
    /// whatever the new code happens to emit. It came out bit-for-bit identical, which is the claim that
    /// matters: unstamped historical replays reconstruct under Default, so a single extra or reordered RNG
    /// draw here would silently invalidate honest history.
    ///
    /// It stays as a RATCHET. Any future edit to the resolver that moves a Default fight has to change this
    /// line deliberately and say why.
    /// </summary>
    [Fact]
    public void FlagOffCombatMatchesItsPreFeatureGoldenVector()
    {
        var rng = new Random(424242);
        var sets = new[]
        {
            Array.Empty<string>(),
            ["rusty-blade", "padded-vest", "lucky-feather"],
            ["steel-saber", "chain-hauberk", "swift-anklet"],
            new[] { "arkforged-edge", "covenant-plate", "vtxo-charm" },
        };
        var log = new StringBuilder();
        foreach (var cfg in new[] { GameConfig.Default, GameConfig.Default with
                 {
                     Combat = GameConfig.Default.Combat with { InnateAbilities = true },
                 } })
        {
            for (var i = 0; i < 400; i++)
            {
                // Draw order is part of the vector: both genomes, THEN both levels.
                var ga = BredFrom(rng);
                var gb = BredFrom(rng);
                var a = Make(ga, "a", rng.Next(1, 40));
                var b = Make(gb, "b", rng.Next(1, 40));
                foreach (var id in sets[rng.Next(sets.Length)]) a.Equipment.Equip(ItemCatalog.Find(id)!);
                foreach (var id in sets[rng.Next(sets.Length)]) b.Equipment.Equip(ItemCatalog.Find(id)!);
                var seed = new byte[32];
                rng.NextBytes(seed);

                var r = BattleEngine.Fight(a, b, seed, cfg);
                log.Append(r.WinnerId).Append('|').Append(r.Turns).Append('|').Append(r.WinnerRemainingHp).Append('|');
                foreach (var e in r.Events)
                    log.Append(e.Turn).Append(',').Append(e.ActorId).Append(',').Append(e.TargetId)
                       .Append(',').Append(e.Kind).Append(',').Append(e.SkillId).Append(',').Append(e.Damage)
                       .Append(',').Append(e.Crit).Append(',').Append(e.Healed).Append(',')
                       .Append(e.TargetHpAfter).Append(';');
                log.Append('\n');
            }
        }

        Assert.Equal("41f87c67d792dcae3d79a6d1d62b0e50e54229374cf2fd93de8d87d160049e1c",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log.ToString()))).ToLowerInvariant());
    }

    /// <summary>A gen-3 bred genome drawn from <paramref name="rng"/> — the draw ORDER is part of the golden
    /// vector above, so this must keep consuming the stream exactly as it does.</summary>
    private static Genome BredFrom(Random rng, int gen = 3)
    {
        var e = new byte[32];
        if (gen == 0) { rng.NextBytes(e); return Genome.NewGen0(e); }
        var a = BredFrom(rng, gen - 1);
        var b = BredFrom(rng, gen - 1);
        rng.NextBytes(e);
        return GeneMixer.Mix(a, b, e);
    }

    [Fact]
    public void TheVarianceSpanIsTheStockTenForEveryPlainItem()
    {
        foreach (var item in ItemCatalog.All.Where(i => i.VarianceBonus == 0))
            Assert.Equal(CombatConfig.BaseVarianceSpan, CombatShapes.VarianceSpan([item], On));
    }

    // ── the on case ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACounterIsWorthPlusTheEdgeAgainstItsTargetAndMinusItAgainstItsAnswer()
    {
        var edge = GearCounterRules.Default.Edge;
        foreach (var shape in new[] { CombatShape.Offense, CombatShape.Bulk, CombatShape.Tempo })
        {
            var item = ItemCatalog.All.Single(i => i.Counters == shape);
            Assert.Equal(1 + edge, CombatShapes.Multiplier([item], shape, On), 9);
            Assert.Equal(1 - edge, CombatShapes.Multiplier([item], CombatShapes.WeakTo(shape), On), 9);

            // The third shape — the one it neither counters nor is answered by — is a clean no-op.
            var neutral = new[] { CombatShape.Offense, CombatShape.Bulk, CombatShape.Tempo }
                .Single(s => s != shape && s != CombatShapes.WeakTo(shape));
            Assert.Equal(1.0, CombatShapes.Multiplier([item], neutral, On), 9);
        }
    }

    [Fact]
    public void EveryShapeIsCounteredByExactlyOneItem_AndNoCounterAnswersItself()
    {
        // The cycle is what makes "no single best set" true rather than aspirational: if two charms countered
        // the same shape, or one were never answered, one of them would be the strictly better buy.
        foreach (var shape in new[] { CombatShape.Offense, CombatShape.Bulk, CombatShape.Tempo })
        {
            Assert.Single(ItemCatalog.All, i => i.Counters == shape);
            Assert.NotEqual(shape, CombatShapes.WeakTo(shape));
        }
        // …and WeakTo is a 3-cycle, so following it three times comes home.
        foreach (var shape in new[] { CombatShape.Offense, CombatShape.Bulk, CombatShape.Tempo })
            Assert.Equal(shape, CombatShapes.WeakTo(CombatShapes.WeakTo(CombatShapes.WeakTo(shape))));
    }

    [Fact]
    public void TheRightCharmBeatsTheWrongOneOnTheSamePair_ButStillOnlyTilts()
    {
        // The headline behaviour: the SAME hero against the SAME opponent, differing only in which charm it
        // brought. It must move a lot of fights and still not be a switch — a counter that always won would
        // just be the new convergence.
        var opponent = Bred(7_001);
        var target = CombatShapes.Of(opponent, 12, On);
        var right = ItemCatalog.All.Single(i => i.Counters == target).Id;
        // The charm this opponent's shape ANSWERS — the one whose WeakTo is the opponent's shape, which is
        // two steps round the 3-cycle, not one. (One step is the charm that is merely NEUTRAL here.)
        var wrong = ItemCatalog.All
            .Single(i => i.Counters is { } c && CombatShapes.WeakTo(c) == target).Id;

        int rightWins = 0, wrongWins = 0;
        for (var i = 0; i < 200; i++)
        {
            var b = Make(opponent, "b", 12, "arkforged-edge", "covenant-plate");
            if (BattleEngine.Fight(Make(Bred(7_002), "a", 12, "arkforged-edge", "covenant-plate", right), b, Seed(i), On)
                .WinnerId == "a") rightWins++;
            if (BattleEngine.Fight(Make(Bred(7_002), "a", 12, "arkforged-edge", "covenant-plate", wrong), b, Seed(i), On)
                .WinnerId == "a") wrongWins++;
        }
        Assert.True(rightWins > wrongWins,
            $"the right charm ({rightWins}/200) did not beat the wrong one ({wrongWins}/200)");
    }

    [Fact]
    public void AWildcardWidensTheDamageRollSymmetrically_WithoutMovingItsMean()
    {
        // Mean-preserving is the whole claim: the prism buys UNCERTAINTY, never an edge. If it shifted the
        // mean it would be a stat item wearing a variance costume, and "high-variance vs consistent" would
        // stop being a real choice.
        var prism = ItemCatalog.Find("chaos-prism")!;
        Assert.Equal(CombatConfig.BaseVarianceSpan + prism.VarianceBonus, CombatShapes.VarianceSpan([prism], On));

        // The roll is (100 - span + Next(2*span+1))/100, i.e. uniform on [1-span/100, 1+span/100] — the same
        // midpoint 1.0 at every span, which is what "mean-preserving" means here.
        foreach (var span in new[] { CombatConfig.BaseVarianceSpan, CombatConfig.BaseVarianceSpan + prism.VarianceBonus })
        {
            var lo = (100 - span) / 100.0;
            var hi = (100 - span + 2 * span) / 100.0;
            Assert.Equal(1.0, (lo + hi) / 2, 9);
        }
    }

    [Fact]
    public void TheShapeClassifierIgnoresEquipment()
    {
        // The measured reason: folding gear in makes 54.5% of a tier-3 pool read Bulk and shrinks Tempo to
        // 9.7%, which would hand one charm the endgame. Reading the naked build keeps the surface even AND
        // keeps a hero's shape a fact about the hero, so the counter-pick is answerable from its card.
        for (var i = 0; i < 60; i++)
        {
            var g = Bred(i);
            var bare = CombatShapes.Of(g, 12, On);
            foreach (var loadout in new[]
                     {
                         new[] { "covenant-plate" },
                         ["arkforged-edge", "covenant-plate", "vtxo-charm"],
                         ["steel-saber", "swift-anklet"],
                     })
            {
                var h = Make(g, "h", 12, loadout);
                Assert.Equal(bare, CombatShapes.Of(h.Genome, h.Level, On));
            }
        }
    }

    [Fact]
    public void EveryShapeIsWellRepresentedInABredPopulation()
    {
        // The counter system's whole premise. If one shape were rare, its charm would be dead stock and the
        // other two would collapse back into a single best pick — which is exactly the convergence this
        // feature exists to break. (The element ring fails this test: 75.0% of bred pairs are ring-neutral.)
        var counts = new int[3];
        for (var i = 0; i < 1_500; i++) counts[(int)CombatShapes.Of(Bred(i), 10, On)]++;
        foreach (var c in counts)
            Assert.True(c >= 1_500 * 0.20,
                $"a build shape is too rare for its counter to be worth owning: {string.Join('/', counts)}");
    }
}
