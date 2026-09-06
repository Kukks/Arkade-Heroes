using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Core.Models.Options;
using NArk.Core.Services;

namespace ArkadeHeroes.Chain.NArk;

/// <summary>
/// Holds back the SDK's automatic coin renewal until a coin is genuinely near the end of its life.
///
/// <para>Arkade coins expire, so the SDK re-boards ("renews") them into a fresh batch before they do.
/// The operator charges an intent fee for that — on this stack <c>amount * 0.01</c> per offchain input,
/// so a renewal of the whole wallet costs 1% of the whole wallet. That is a fair price to keep coins
/// alive; it is not a fair price for opening a tab.</para>
///
/// <para><see cref="NArk.Core.Services.SimpleIntentScheduler"/> renews whenever
/// <c>expiry - threshold &lt; now</c> against a single configured threshold. That is the right rule only
/// while the threshold is short relative to how long a coin actually lives. It isn't here: the wallet
/// configures one day and this stack's coins live under half an hour, so the test was true from the
/// moment a coin was born and every cycle renewed the entire balance again — including the cycle the
/// SDK runs the instant it starts, which the wallet does on every WASM boot. Players watched thousands
/// of sats disappear across a page reload with nothing in the UI to explain it.</para>
///
/// <para>So this decorator adds a second, lifetime-RELATIVE condition: only offer a coin to the inner
/// scheduler once it is inside the last <see cref="RenewWithinLastFractionOfLife"/> of its own life. It
/// can only ever renew FEWER coins than the inner scheduler would, never more, and on a long-lived
/// (mainnet-shaped) coin the configured threshold is still the binding constraint, so behaviour there
/// is unchanged. Anything it cannot reason about — a recoverable or unrolled coin, a height-only
/// expiry, a nonsensical lifetime — is passed straight through, because the cost of renewing early is
/// a fee and the cost of renewing too late is the coin.</para>
/// </summary>
public sealed class BatchRenewalScheduler(IIntentScheduler inner, IBitcoinBlockchain blockchain) : IIntentScheduler
{
    /// <summary>How much of a coin's life must be gone before renewing it is worth the operator's fee.</summary>
    public const double RenewWithinLastFractionOfLife = 0.25;

    /// <summary>
    /// How often the SDK's intent-generation loop should run a cycle. Deliberately shorter than the
    /// SDK's five-minute default: the loop is the only thing that ever notices a coin has become due, so
    /// its period is the latency between "due" and "renewed" — including after a hidden tab's timers have
    /// been throttled, or after a frozen tab wakes. Cycles themselves are free; only a renewal costs a
    /// fee, and <see cref="IsRenewalDue"/> is what decides that, so polling more often costs nothing.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A floor under the fraction window, so a short-lived coin is still renewed with room to spare.
    ///
    /// <para>This is the number that decides how long the wallet can be unable to act without a coin
    /// expiring: a coin is renewed once it is inside this window, so in the worst case — a coin sitting
    /// just outside it when the tab is hidden — the tab may be frozen for this long before the coin
    /// lapses. Renewing on every cycle (the old behaviour) bought a whole coin lifetime of that
    /// tolerance; this is what buys it back, and on any server whose coins live for weeks it costs
    /// nothing at all, because there the configured threshold is still what binds.</para>
    /// </summary>
    public static readonly TimeSpan MinimumRenewalWindow = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyCollection<ArkIntentSpec>> GetIntentsToSubmit(
        IReadOnlyCollection<ArkCoin> unspentVtxos, CancellationToken cancellationToken = default)
    {
        var now = await blockchain.GetChainTime(cancellationToken);
        var due = unspentVtxos.Where(coin => IsRenewalDue(coin, now)).ToArray();
        return due.Length == 0 ? [] : await inner.GetIntentsToSubmit(due, cancellationToken);
    }

    /// <summary>
    /// True when this coin is close enough to expiry to be worth the renewal fee — or when the coin is
    /// one the guard has no business delaying.
    /// </summary>
    private static bool IsRenewalDue(ArkCoin coin, TimeHeight now)
    {
        // Recoverable coins must join a batch to be recovered at all, and unrolled coins are racing the
        // unilateral-exit delay. Both are the inner scheduler's "batch this ASAP" cases — never delay them.
        if (coin.IsRecoverable(now) || coin.Unrolled)
            return true;

        // Height-gated expiry: no wall-clock lifetime to measure, so leave the decision to the inner
        // scheduler's own height threshold.
        if (coin.ExpiresAt is not { } expiry)
            return true;

        // A lifetime that isn't positive means the birth or expiry we were given cannot be trusted
        // (clock skew, a storage default). Don't read that as "plenty of life left".
        var life = expiry - coin.Birth;
        if (life <= TimeSpan.Zero)
            return true;

        var window = life * RenewWithinLastFractionOfLife;
        if (window < MinimumRenewalWindow)
            window = MinimumRenewalWindow;

        return expiry - now.Timestamp <= window;
    }
}

/// <summary>
/// The renewal policy every wallet here composes, as a pair: with no threshold
/// <see cref="SimpleIntentScheduler"/> throws each cycle and coins expire unrenewed; with a threshold
/// but no <see cref="BatchRenewalScheduler"/> guard it re-boards everything each cycle at 1% per input.
/// </summary>
public static class ArkadeRenewalRegistration
{
    public static readonly TimeSpan RenewalThreshold = TimeSpan.FromDays(1);

    public static IServiceCollection AddArkadeRenewalScheduling(this IServiceCollection services)
    {
        services.AddSingleton<SimpleIntentScheduler>();
        services.Configure<SimpleIntentSchedulerOptions>(opts => opts.Threshold = RenewalThreshold);
        services.AddSingleton<IIntentScheduler>(sp => new BatchRenewalScheduler(
            sp.GetRequiredService<SimpleIntentScheduler>(),
            sp.GetRequiredService<IBitcoinBlockchain>()));
        services.Configure<IntentGenerationServiceOptions>(
            opts => opts.PollInterval = BatchRenewalScheduler.PollInterval);
        return services;
    }
}
