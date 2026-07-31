namespace ArkadeHeroes.Core.Genetics;

/// <summary>
/// What a starter claim mints, and what it costs.
///
/// <para>Starter heroes are bought, not given — including the first. Free ones meant anyone who could
/// generate a keypair could mint real assets, and a keypair costs nothing, so the giveaway scaled with the
/// attacker rather than with the playerbase.</para>
///
/// <para>The price is not a new number. A claimed hero costs exactly what a bred one costs at the floor —
/// the breed fee with no prior breeds behind it — so the cheapest a hero can be made is the same however
/// you make one. Derived from the breed fee on purpose: a separate knob would let the two drift, and
/// "claiming costs the same as breeding" would quietly stop being true without anything failing.</para>
///
/// <para>Nor is a claim one-time. It can be made as often as the player is willing to pay for it, which
/// makes recruits a permanent floor under hero supply rather than a one-off handout. What stops that being
/// an inflation valve is <see cref="RecruitStatCap"/>: bought heroes are deliberately the worst in the
/// game, so buying more of them never competes with breeding better ones.</para>
/// </summary>
public static class StarterPolicy
{
    /// <summary>How many generation-0 heroes one claim mints. Two, so a new player can breed immediately.</summary>
    public const int HeroCount = 2;

    /// <summary>
    /// The ceiling on a recruit's stat and growth genes, out of 255.
    ///
    /// <para>Recruits are repeatable — pay again, get more — so the roll cannot be worth farming. At a
    /// quarter of the range a recruit averages well under a bred hero and can never spike, which is the
    /// point: the cheap on-ramp stays an on-ramp, and anything actually good has to be bred, fused, or
    /// bought from another player.</para>
    /// </summary>
    public const byte RecruitStatCap = 63;

    /// <summary>The whole claim's fee: the floor price of a hero, once per hero minted.</summary>
    public static long ClaimFeeSats(GameConfig? config = null)
    {
        var c = config ?? GameConfig.Default;
        return BreedingPolicy.FeeSats(c.BreedingFeeSats, 0, c) * HeroCount;
    }
}
