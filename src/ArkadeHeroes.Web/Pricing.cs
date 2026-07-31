using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Web;

/// <summary>
/// What an action costs, computed from the SERVER's published <see cref="GameConfigDto"/> so a page can
/// state the price before the player commits real sats to it.
///
/// <para>Every fee here is already decided by <c>ArkadeHeroes.Core</c> — <c>Leveling.MatchFee</c>,
/// <c>Gauntlet.Fee</c>, <c>BreedingPolicy.FeeSats</c>. This is a restatement of those formulas over the DTO
/// the browser actually holds, because Core's versions take a <c>GameConfig</c> and no DTO→config
/// conversion exists. Restating them is a drift risk, so <c>PricingTests</c> re-derives every one of these
/// from Core on each build and fails the suite if the two ever disagree — the same discipline
/// <see cref="LandingGenomes"/> uses for the landing art.</para>
///
/// <para>Everything returns <c>long?</c> and every entry point is null-tolerant: an unread config yields
/// <c>null</c>, which pages must render as "no price stated" rather than as a confident zero. A quoted
/// "0 sat" on an action that charges 1,000 is worse than quoting nothing, because the player believes it.</para>
/// </summary>
internal static class Pricing
{
    /// <summary>The per-character match fee a staked duel or squad match charges EACH side, on top of the
    /// wager. Mirrors <c>Leveling.MatchFee</c>.</summary>
    public static long? MatchFee(GameConfigDto? config, int heroLevel) =>
        config is not { } c ? null : c.MatchFeeBaseSats + c.MatchFeePerLevel * Math.Max(1, heroLevel);

    /// <summary>Sats to enter the gauntlet: a same-level match fee plus the dungeon's entry premium.
    /// Mirrors <c>Gauntlet.Fee</c>. The premium is a content-pack constant rather than a config knob, so it
    /// is read straight from Core — the same value the server prices with.</summary>
    public static long? GauntletFee(GameConfigDto? config, int heroLevel) =>
        MatchFee(config, heroLevel) is { } fee
            ? fee + ArkadeHeroes.Core.Progression.Gauntlet.FeeBonusSats
            : null;

    /// <summary>What breeding this PAIR costs. The fee doubles per prior breed across both parents and
    /// stops doubling at the configured cap, so a well-used pair can cost many multiples of the base —
    /// which is precisely why the number has to be on screen. Mirrors <c>BreedingPolicy.FeeSats</c>.</summary>
    public static long? BreedFee(GameConfigDto? config, int parentABreeds, int parentBBreeds)
    {
        if (config is not { } c) return null;
        var breeds = Math.Max(parentABreeds + parentBBreeds, 0);
        return c.BreedingFeeSats * (1L << Math.Min(breeds, c.BreedFeeDoublingCap));
    }

    /// <summary>The flat fusion fee. Flat today, but read from config rather than compiled in so an
    /// operator retuning it retunes what the page says too.</summary>
    public static long? MergeFee(GameConfigDto? config) => config?.MergeFeeSats;

    /// <summary>A sats amount for display, or null when the price genuinely isn't known.</summary>
    public static string? Sats(long? amount) => amount?.ToString("N0");
}
