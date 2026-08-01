using NArk.Abstractions.Fees;
using NArk.Core.Services;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// What it costs to keep this wallet's coins alive, in the player's own numbers.
///
/// <para>Arkade coins expire, so the wallet re-boards ("renews") them into a fresh batch before they do,
/// and the Arkade operator charges an intent fee to take them in. On this stack that fee is a percentage
/// of every coin renewed, so it is not small and it is not fixed — it scales with the balance. Players
/// watched thousands of sats leave a wallet they had not spent from, with nothing anywhere in the UI to
/// name the charge. Money that leaves without an explanation reads as theft, so the charge gets a name.</para>
///
/// <para>The number is never invented here: it comes from the SDK's own <see cref="IFeeEstimator"/>,
/// evaluating the operator's fee expression from the server's advertised terms against this wallet's
/// actual coins — the same call, with the same empty output set, that
/// <see cref="NArk.Core.Services.SimpleIntentScheduler"/> makes when it decides what a renewal will cost.
/// So the quote cannot drift from what is really charged, and it follows the operator if they change it.</para>
/// </summary>
public class RenewalUpkeep(ISpendingService spendingService, IFeeEstimator feeEstimator)
{
    /// <summary>
    /// What the operator would charge to renew everything this wallet holds right now, or null when the
    /// wallet holds nothing or the estimate can't be read (the UI then simply says nothing rather than
    /// guessing — a wrong number here is worse than no number).
    /// </summary>
    public virtual async Task<RenewalQuote?> QuoteAsync(string walletId, CancellationToken ct = default)
    {
        try
        {
            var coins = (await spendingService.GetAvailableCoins(walletId)).ToArray();
            if (coins.Length == 0) return null;

            var amount = coins.Sum(c => c.Amount.Satoshi);
            if (amount <= 0) return null;

            // Empty outputs: an intent that only re-boards its inputs, which is what a renewal is.
            var fee = await feeEstimator.EstimateFeeAsync(coins, [], ct);
            return fee <= 0 ? null : new RenewalQuote(amount, fee);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The cost of one renewal of <paramref name="AmountSats"/>, as the operator prices it today.</summary>
/// <param name="AmountSats">The coin value the renewal would take in.</param>
/// <param name="FeeSats">What the operator charges to take it in.</param>
public readonly record struct RenewalQuote(long AmountSats, long FeeSats)
{
    /// <summary>The fee as a percentage of the amount renewed — derived, not assumed.</summary>
    public double Percent => AmountSats <= 0 ? 0 : (double)FeeSats / AmountSats * 100d;

    /// <summary>
    /// One line naming the charge, what it buys, and who takes it. Deliberately says "expire" rather than
    /// anything softer: the reason the fee exists is that the coins would otherwise stop being spendable.
    /// </summary>
    public string Summary =>
        $"{FeeSats:N0} sat (about {Percent:0.##}%) — what the Arkade operator charges to renew this " +
        "balance before your coins expire. Charged when a renewal actually happens, not per visit.";
}
