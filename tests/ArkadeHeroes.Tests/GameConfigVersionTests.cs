using System.Globalization;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The version identifier a replay is stamped with: it must be a pure function of the config's
/// verification-critical rules, identical on every host and in every locale, and it must round-trip through
/// the wire DTO so a client can fetch the rules a stamp names and CHECK them rather than trust them.
/// </summary>
public class GameConfigVersionTests
{
    [Fact]
    public void Compute_IsDeterministicAndPinnedToDefault()
    {
        // The same config always yields the same id, and Default's id is the cached constant every
        // pre-stamp replay resolves against.
        Assert.Equal(GameConfigVersion.Compute(GameConfig.Default), GameConfigVersion.Compute(GameConfig.Default));
        Assert.Equal(GameConfigVersion.Default, GameConfigVersion.Compute(GameConfig.Default));
        Assert.Equal(64, GameConfigVersion.Default.Length);
        Assert.Equal(GameConfigVersion.Default.ToLowerInvariant(), GameConfigVersion.Default);
    }

    [Fact]
    public void Compute_IsCultureInvariant()
    {
        // The bug class this guards: a locale whose decimal separator is ',' and whose digits/negatives
        // format differently would otherwise give a client a DIFFERENT id for the SAME rules, and every
        // replay it fetched would 404. Run the same computation under hostile cultures and demand equality.
        // (Same discipline as the InvariantCulture fix for signed preimages.)
        var expected = GameConfigVersion.Compute(GameConfig.Default);
        var tuned = GameConfig.Default with
        {
            Combat = GameConfig.Default.Combat with { InnateAbilities = true, ElementStrong = 1.45 },
        };
        var expectedTuned = GameConfigVersion.Compute(tuned);

        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "fr-FR", "tr-TR", "ar-SA", "th-TH" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(expected, GameConfigVersion.Compute(GameConfig.Default));
                Assert.Equal(expectedTuned, GameConfigVersion.Compute(tuned));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Compute_ChangesWhenAnyVerificationCriticalValueChanges()
    {
        // Every rule a deterministic replay reads must move the id — otherwise two configs that fight
        // DIFFERENTLY would share a stamp, and the endpoint would hand back the wrong rules.
        var d = GameConfig.Default;
        var variants = new (string Name, GameConfig Config)[]
        {
            ("absorb", d with { Absorb = new AbsorbOdds(101, 90) }),
            ("gene", d with { Gene = new GeneConfig(247, 250) }),
            ("fusion", d with { FusionConcentrateThreshold = 216 }),
            ("sterility", d with { Sterility = new SterilityChances(49, 30, 15, 5) }),
            ("rarity", d with { Rarity = d.Rarity with { LegendaryWeight = 51 } }),
            ("affinity", d with { Affinity = d.Affinity with { Cap = 0.06 } }),
            ("curve", d with { Curve = d.Curve with { Exponent = 1.36 } }),
            ("maxTurns", d with { Combat = d.Combat with { MaxTurns = 61 } }),
            ("elementStrong", d with { Combat = d.Combat with { ElementStrong = 1.31 } }),
            ("policy", d with { Combat = d.Combat with { SelectionPolicy = CombatSelectionPolicy.Greedy } }),
            ("elementAware", d with { Combat = d.Combat with { ElementAwareSelection = true } }),
            ("innateFlag", d with { Combat = d.Combat with { InnateAbilities = true } }),
            ("squadSynergy", d with { Combat = d.Combat with { SquadSynergy = true } }),
            ("innateKnob", d with
            {
                Combat = d.Combat with { Innate = InnateBonuses.Default with { Ward = 0.26 } },
            }),
        };

        var ids = new HashSet<string> { GameConfigVersion.Default };
        foreach (var (name, config) in variants)
        {
            var id = GameConfigVersion.Compute(config);
            Assert.True(id != GameConfigVersion.Default, $"changing {name} did not change the version id");
            Assert.True(ids.Add(id), $"{name} collided with another config's version id");
        }
    }

    /// <summary>
    /// The exhaustive version of the test above, by reflection over the primary constructors — so a
    /// verification-critical value ADDED LATER and forgotten in the canonical writer fails here instead of
    /// shipping. That failure mode is the severe one: two configs that fight differently would share a
    /// stamp, and GET /api/config/{version} would hand a verifier the wrong rules with full confidence.
    /// </summary>
    [Theory]
    [InlineData(nameof(GameConfig.Absorb))]
    [InlineData(nameof(GameConfig.Gene))]
    [InlineData(nameof(GameConfig.Sterility))]
    [InlineData(nameof(GameConfig.Rarity))]
    [InlineData(nameof(GameConfig.Affinity))]
    [InlineData(nameof(GameConfig.Curve))]
    [InlineData(nameof(GameConfig.Combat))]
    public void Compute_ChangesForEVERYMemberOfEveryVerificationCriticalRecord(string member)
    {
        var d = GameConfig.Default;
        var current = typeof(GameConfig).GetProperty(member)!.GetValue(d)!;

        foreach (var perturbed in PerturbEachParameter(current))
        {
            var config = d with { };
            typeof(GameConfig).GetProperty(member)!.SetValue(config, perturbed.Value);
            Assert.True(
                GameConfigVersion.Compute(config) != GameConfigVersion.Default,
                $"{member}.{perturbed.Parameter} is not part of the version id — a config that fights " +
                "differently would share a stamp with Default");
        }
    }

    [Fact]
    public void Compute_ChangesForEveryInnateKnob()
    {
        // CombatConfig.Innate is a nested record the writer resolves through InnateOrDefault, so its members
        // need the same exhaustive sweep.
        foreach (var perturbed in PerturbEachParameter(InnateBonuses.Default))
        {
            var config = GameConfig.Default with
            {
                Combat = GameConfig.Default.Combat with { Innate = (InnateBonuses)perturbed.Value },
            };
            Assert.True(
                GameConfigVersion.Compute(config) != GameConfigVersion.Default,
                $"InnateBonuses.{perturbed.Parameter} is not part of the version id");
        }
    }

    [Fact]
    public void Compute_ChangesForTheStandaloneFusionThreshold()
    {
        Assert.NotEqual(GameConfigVersion.Default,
            GameConfigVersion.Compute(GameConfig.Default with { FusionConcentrateThreshold = 216 }));
    }

    /// <summary>Yields one copy of <paramref name="record"/> per primary-constructor parameter, with that
    /// one parameter changed to a different value and every other left alone.</summary>
    private static IEnumerable<(string Parameter, object Value)> PerturbEachParameter(object record)
    {
        var type = record.GetType();
        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var parameters = ctor.GetParameters();

        for (var i = 0; i < parameters.Length; i++)
        {
            var args = parameters
                .Select(p => type.GetProperty(p.Name!)!.GetValue(record))
                .ToArray();
            args[i] = Perturb(parameters[i].ParameterType, args[i]);
            yield return (parameters[i].Name!, ctor.Invoke(args));
        }
    }

    private static object Perturb(Type type, object? value) => type switch
    {
        _ when type == typeof(byte) => (byte)((byte)value! + 1),          // 255 wraps to 0 — still a change
        _ when type == typeof(int) => (int)value! + 1,
        _ when type == typeof(long) => (long)value! + 1,
        _ when type == typeof(double) => (double)value! + 0.125,
        _ when type == typeof(bool) => !(bool)value!,
        _ when type == typeof(CombatSelectionPolicy) =>
            (CombatSelectionPolicy)value! == CombatSelectionPolicy.Greedy
                ? CombatSelectionPolicy.Tactical
                : CombatSelectionPolicy.Greedy,
        // A null Innate and an explicit InnateBonuses.Default are the SAME rules by design, so perturbing
        // this slot means supplying knobs that genuinely differ.
        _ when type == typeof(InnateBonuses) => InnateBonuses.Default with { Ward = 0.99 },
        _ => throw new InvalidOperationException(
            $"No perturbation defined for {type.Name} — add one so this sweep stays exhaustive."),
    };

    [Fact]
    public void Compute_IgnoresTheEconomy_SoAFeeChangeStrandsNoReplay()
    {
        // The deliberate exclusion. Economy values are GameOptions-tunable at runtime and unread by every
        // resolver, so folding them into the id would 404 every already-stamped replay the moment an
        // operator changed a fee — breaking honest history over a value no battle log depends on.
        var retuned = GameConfig.Default with
        {
            BreedingFeeSats = 9_999,
            MatchFeeBaseSats = 4_242,
            OfferListingFeeSats = 7_000,
            TournamentRakePct = 25,
            MatchmakingTake = 99,
        };
        Assert.Equal(GameConfigVersion.Default, GameConfigVersion.Compute(retuned));
    }

    [Fact]
    public void Compute_TreatsANullInnateAsTheDefaultKnobs()
    {
        // CombatConfig.InnateOrDefault makes these two spellings ONE behaviour, so they must be one id —
        // otherwise a server writing it one way and a client the other would never agree on a stamp.
        var omitted = GameConfig.Default with { Combat = GameConfig.Default.Combat with { Innate = null } };
        var explicitly = GameConfig.Default with
        {
            Combat = GameConfig.Default.Combat with { Innate = InnateBonuses.Default },
        };
        Assert.Equal(GameConfigVersion.Compute(omitted), GameConfigVersion.Compute(explicitly));
    }

    [Fact]
    public void GameRulesDto_RoundTripsBackToItsOwnVersion()
    {
        // The property the client's trustless check rests on: whatever the endpoint serves for a version
        // must rebuild a config that HASHES to that same version. If this ever stopped holding, every
        // fetch would be refused and no stamped replay could verify.
        foreach (var config in new[]
                 {
                     GameConfig.Default,
                     GameConfig.Default with { Combat = GameConfig.Default.Combat with { InnateAbilities = true } },
                     GameConfig.Default with
                     {
                         Absorb = new AbsorbOdds(200, 12),
                         Affinity = new AffinityBonuses(0.4, 0.3, 0.2, 0.1, 0.05, 0.9),
                         Curve = new XpCurve(123, 46.5, 1.42, 40),
                         Combat = GameConfig.Default.Combat with
                         {
                             MaxTurns = 33,
                             SelectionPolicy = CombatSelectionPolicy.Greedy,
                             ElementAwareSelection = true,
                             InnateAbilities = true,
                             SquadSynergy = true,
                             Innate = InnateBonuses.Default with { Ward = 0.44, BrandTurns = 5 },
                         },
                     },
                 })
        {
            var dto = GameRulesDto.From(config);
            Assert.Equal(GameConfigVersion.Compute(config), dto.Version);

            var rebuilt = dto.ToGameConfig();
            Assert.NotNull(rebuilt);
            Assert.Equal(dto.Version, GameConfigVersion.Compute(rebuilt!));

            // The rebuilt config must be the same RULES, not merely the same hash.
            Assert.Equal(config.Absorb, rebuilt!.Absorb);
            Assert.Equal(config.Gene, rebuilt.Gene);
            Assert.Equal(config.Sterility, rebuilt.Sterility);
            Assert.Equal(config.Rarity, rebuilt.Rarity);
            Assert.Equal(config.Affinity, rebuilt.Affinity);
            Assert.Equal(config.Curve, rebuilt.Curve);
            Assert.Equal(config.Combat.InnateOrDefault, rebuilt.Combat.InnateOrDefault);
            Assert.Equal(config.Combat with { Innate = null }, rebuilt.Combat with { Innate = null });
        }
    }

    [Fact]
    public void GameRulesDto_RefusesAnUnknownSelectionPolicy()
    {
        // A policy this client does not know would replay a DIFFERENT fight. Refuse explicitly (null)
        // rather than quietly substituting one — a silent substitution is the bug this work removes.
        var dto = GameRulesDto.From(GameConfig.Default) with { SelectionPolicy = "Clairvoyant" };
        Assert.Null(dto.ToGameConfig());
    }

    [Fact]
    public void GameConfigDto_PublishesTheCurrentVersion()
    {
        // What the doc comment on GameConfigDto promised and the wire never carried: the running server's
        // rules version, so a client can pre-warm and can tell a default-rules server from a retuned one.
        Assert.Equal(GameConfigVersion.Default, GameConfigDto.From(GameConfig.Default).Version);
        var retuned = GameConfig.Default with { Absorb = new AbsorbOdds(200, 90) };
        Assert.NotEqual(GameConfigVersion.Default, GameConfigDto.From(retuned).Version);
    }
}
