using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The REAL <see cref="Pricing"/>, checked against Core's own fee functions.
///
/// <para>The sibling suite in <c>ArkadeHeroes.Tests</c> restates these formulas by hand, because that
/// project cannot reference the WASM app. This one can — it renders its pages — so it closes the gap that
/// left: a drift between <c>Pricing.cs</c> and its restatement was invisible, and <c>Pricing.cs</c> is the
/// copy a player actually reads a price from before spending real bitcoin.</para>
/// </summary>
public class PricingTests
{
    private static GameConfigDto Dto(GameConfig? c = null) => GameConfigDto.From(c ?? GameConfig.Default);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(25)]
    [InlineData(60)]
    public void TheQuotedMatchFee_IsWhatTheServerCharges(int level) =>
        Assert.Equal(Leveling.MatchFee(level, GameConfig.Default), Pricing.MatchFee(Dto(), level));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TheQuotedMatchFee_ClampsSubLevelOneTheWayCoreDoes(int level) =>
        Assert.Equal(Leveling.MatchFee(level, GameConfig.Default), Pricing.MatchFee(Dto(), level));

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void TheQuotedGauntletEntry_IsWhatTheServerCharges(int level) =>
        Assert.Equal(Gauntlet.Fee(level, GameConfig.Default), Pricing.GauntletFee(Dto(), level));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(20, 20)]   // well past the doubling cap — an unclamped shift is the failure this catches
    public void TheQuotedBreedFee_EscalatesAndCapsTheWayCoreDoes(int aBreeds, int bBreeds)
    {
        var c = GameConfig.Default;
        Assert.Equal(
            BreedingPolicy.FeeSats(c.BreedingFeeSats, aBreeds + bBreeds, c),
            Pricing.BreedFee(Dto(c), aBreeds, bBreeds));
    }

    [Fact]
    public void ARetunedConfig_MovesEveryQuote()
    {
        var retuned = GameConfig.Default with { MatchFeeBaseSats = 777, MatchFeePerLevel = 13, BreedingFeeSats = 9_999 };
        var dto = Dto(retuned);

        Assert.Equal(Leveling.MatchFee(5, retuned), Pricing.MatchFee(dto, 5));
        Assert.Equal(Gauntlet.Fee(5, retuned), Pricing.GauntletFee(dto, 5));
        Assert.Equal(BreedingPolicy.FeeSats(retuned.BreedingFeeSats, 1, retuned), Pricing.BreedFee(dto, 1, 0));
        Assert.NotEqual(Pricing.MatchFee(Dto(), 5), Pricing.MatchFee(dto, 5));
    }

    [Fact]
    public void AnUnreadConfigQuotesNothingRatherThanAConfidentZero()
    {
        // A quoted "0 sat" on an action that charges 1,000 is worse than quoting nothing, because the
        // player believes it.
        Assert.Null(Pricing.MatchFee(null, 5));
        Assert.Null(Pricing.GauntletFee(null, 5));
        Assert.Null(Pricing.BreedFee(null, 0, 0));
        Assert.Null(Pricing.MergeFee(null));
        Assert.Null(Pricing.Sats(null));
    }
}
