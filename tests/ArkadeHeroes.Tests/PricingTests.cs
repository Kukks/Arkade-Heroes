using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The browser quotes fees before a player commits real bitcoin to an action, and it computes them from
/// the server's published <see cref="GameConfigDto"/> — because Core's own fee functions take a
/// <c>GameConfig</c> and no DTO→config conversion exists. That restatement is a drift risk: retune
/// <c>MatchFeePerLevel</c> in Core and the arena would keep charging the new price while the page kept
/// quoting the old one, authoritatively.
///
/// <para>So these re-derive every quoted fee from Core itself, exactly as <c>LandingGenomeTests</c> does
/// for the landing art. If one fails, the page's arithmetic is wrong — fix
/// <c>src/ArkadeHeroes.Web/Pricing.cs</c>, do not relax the assertion.</para>
///
/// <para>ArkadeHeroes.Web is outside this project's reference closure (it is a WASM app), so the formulas
/// are restated here once more and checked against Core. Note what that does NOT cover: this pins
/// Core against the copy below, so <c>Pricing.cs</c> drifting from BOTH is invisible here. The real
/// methods are called by <c>ArkadeHeroes.Tests.Web.PricingTests</c>, which can reference the app; put a
/// new fee's authoritative check there, and treat this file as the Core-side half.</para>
/// </summary>
public class PricingTests
{
    private static GameConfigDto Dto(GameConfig c) => GameConfigDto.From(c);

    /// <summary>Pricing.MatchFee — what a staked duel or squad match charges each side on top of the wager.</summary>
    private static long MatchFee(GameConfigDto c, int level) =>
        c.MatchFeeBaseSats + c.MatchFeePerLevel * Math.Max(1, level);

    /// <summary>Pricing.GauntletFee.</summary>
    private static long GauntletFee(GameConfigDto c, int level) => MatchFee(c, level) + Gauntlet.FeeBonusSats;

    /// <summary>Pricing.BreedFee.</summary>
    private static long BreedFee(GameConfigDto c, int aBreeds, int bBreeds) =>
        c.BreedingFeeSats * (1L << Math.Min(Math.Max(aBreeds + bBreeds, 0), c.BreedFeeDoublingCap));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(60)]
    public void TheQuotedMatchFee_IsTheFeeTheServerCharges(int level)
    {
        var config = GameConfig.Default;
        Assert.Equal(Leveling.MatchFee(level, config), MatchFee(Dto(config), level));
    }

    /// <summary>Level 0 and below are clamped to 1 by Core; the quote must clamp identically or it would
    /// advertise a cheaper entry than the server bills.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TheQuotedMatchFee_ClampsSubLevelOneTheSameWayCoreDoes(int level)
    {
        var config = GameConfig.Default;
        Assert.Equal(Leveling.MatchFee(level, config), MatchFee(Dto(config), level));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(30)]
    public void TheQuotedGauntletEntry_IsTheFeeTheServerCharges(int level)
    {
        var config = GameConfig.Default;
        Assert.Equal(Gauntlet.Fee(level, config), GauntletFee(Dto(config), level));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(4, 4)]
    [InlineData(20, 20)]   // well past the doubling cap — must plateau, not overflow
    public void TheQuotedBreedFee_IsTheEscalatingFeeTheServerCharges(int aBreeds, int bBreeds)
    {
        var config = GameConfig.Default;
        Assert.Equal(
            BreedingPolicy.FeeSats(config.BreedingFeeSats, aBreeds + bBreeds, config),
            BreedFee(Dto(config), aBreeds, bBreeds));
    }

    [Fact]
    public void TheQuotedMergeFee_IsTheFeeTheServerCharges()
    {
        var config = GameConfig.Default;
        Assert.Equal(config.MergeFeeSats, Dto(config).MergeFeeSats);
    }

    /// <summary>
    /// A retuned operator config must move the quote with it. This is the failure the whole file exists to
    /// catch: a page quoting the DEFAULT price on a server running something else.
    /// </summary>
    [Fact]
    public void ARetunedConfig_MovesEveryQuote()
    {
        var tuned = GameConfig.Default with
        {
            MatchFeeBaseSats = 1_234,
            MatchFeePerLevel = 77,
            BreedingFeeSats = 4_000,
            MergeFeeSats = 9_999,
        };
        var dto = Dto(tuned);

        Assert.Equal(Leveling.MatchFee(5, tuned), MatchFee(dto, 5));
        Assert.NotEqual(Leveling.MatchFee(5, GameConfig.Default), MatchFee(dto, 5));
        Assert.Equal(Gauntlet.Fee(5, tuned), GauntletFee(dto, 5));
        Assert.Equal(BreedingPolicy.FeeSats(tuned.BreedingFeeSats, 2, tuned), BreedFee(dto, 1, 1));
        Assert.Equal(9_999, dto.MergeFeeSats);
    }

    /// <summary>
    /// Everything the quotes need must actually be PUBLISHED. If a fee input stops crossing the wire the
    /// browser cannot price the action at all, and the honest fallback is to quote nothing — which is a
    /// regression worth failing for rather than discovering in the UI.
    /// </summary>
    [Fact]
    public void EveryInputTheBrowserPricesFrom_IsPublishedInTheConfigDto()
    {
        var dto = Dto(GameConfig.Default);
        Assert.True(dto.MatchFeeBaseSats > 0, "MatchFeeBaseSats must reach the client to price a duel.");
        Assert.True(dto.MatchFeePerLevel > 0, "MatchFeePerLevel must reach the client to price a duel.");
        Assert.True(dto.BreedingFeeSats > 0, "BreedingFeeSats must reach the client to price a breed.");
        Assert.True(dto.MergeFeeSats > 0, "MergeFeeSats must reach the client to price a fusion.");
        Assert.True(dto.BreedFeeDoublingCap > 0, "BreedFeeDoublingCap must reach the client to price a breed.");
    }

    /// <summary>
    /// Was pinned here as a known gap — the one real-sats action the browser could not quote — with the note
    /// that publishing the multipliers should make the page quote a price. This asserts that resolution.
    /// </summary>
    [Fact]
    public void TheDeathMatchFee_IsQuotableByTheBrowser()
    {
        var dto = Dto(GameConfig.Default);
        var config = GameConfig.Default;

        Assert.Equal(config.DeathMatchFeeMultiplier, dto.DeathMatchFeeMultiplier);
        Assert.Equal(config.AbsorbFeeMultiplier, dto.AbsorbFeeMultiplier);

        foreach (var absorb in new[] { false, true })
        {
            var fromDto = (absorb ? dto.AbsorbFeeMultiplier : dto.DeathMatchFeeMultiplier)
                          * (dto.MatchFeeBaseSats + dto.MatchFeePerLevel * 5);
            Assert.Equal(Leveling.DeathMatchFee(5, absorb, config), fromDto);
        }

        Assert.True(Leveling.DeathMatchFee(5, absorb: false, config) > Leveling.MatchFee(5, config),
            "A death-match costs more than a wager match.");
        Assert.True(Leveling.DeathMatchFee(5, absorb: true, config) > Leveling.DeathMatchFee(5, false, config),
            "Absorb mode costs more still, which the page says.");
    }
}
