using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Buying starter heroes against the REAL stack: quote → pay from the player's OWN wallet → claim.
///
/// <para>Starter heroes are bought, not given (<see cref="StarterPolicy"/>), so <c>POST /heroes/starter</c>
/// is refused outright until an invoice from <c>POST /heroes/starter/quote</c> has been paid. In NArk mode
/// there is no dev pay-invoice facade to shortcut that — those endpoints only exist on the InMemory chain —
/// so the fee is settled the way a player settles it: real sats, out of a real self-custody wallet, into
/// the treasury-derived address the invoice names.</para>
///
/// <para>And a claim mints ONE hero (<see cref="StarterPolicy.HeroCount"/>), repeatably. A test that wants
/// a breedable pair therefore buys twice, exactly as a player would — which is why the count is a parameter
/// rather than something the caller can assume.</para>
/// </summary>
internal static class StarterPurchaseHelpers
{
    /// <summary>
    /// Buys <paramref name="count"/> heroes for the player, one purchase at a time: quote → pay → claim.
    /// The wallet must already hold spendable sats — fund it BEFORE calling this, not after.
    /// </summary>
    public static async Task<List<HeroDto>> RecruitAsync(
        this ArkadeHeroesClient client, SelfCustodyWallet wallet, int count = StarterPolicy.HeroCount)
    {
        var heroes = new List<HeroDto>();
        for (var bought = 0; bought < count; bought += StarterPolicy.HeroCount)
        {
            await PayForStartersAsync(client, wallet);
            heroes.AddRange(await ClaimWhenFeeClearsAsync(client));
        }
        return heroes;
    }

    /// <summary>
    /// Quotes one starter claim and pays the invoice from the player's wallet. Returns without sending
    /// anything on a free server (breed fee zeroed ⇒ claim fee zeroed), where there is no invoice at all.
    /// </summary>
    public static async Task PayForStartersAsync(this ArkadeHeroesClient client, SelfCustodyWallet wallet)
    {
        var quote = await client.Heroes.RequestStartersAsync();
        if (quote.Fee is { } fee) await wallet.SendAsync(fee.PayToAddress, fee.AmountSats);
    }

    /// <summary>
    /// Claims the heroes a paid quote bought, retrying only while the server has yet to SEE the payment.
    ///
    /// <para>The fee clears asynchronously — the server decides an invoice is paid by polling the invoice
    /// script's VTXOs — so the first claim after a send is expected to be refused. Only that refusal is
    /// retried: any other rule failure is a real one and surfaces immediately rather than being sat on
    /// until the deadline.</para>
    /// </summary>
    public static async Task<IReadOnlyList<HeroDto>> ClaimWhenFeeClearsAsync(
        this ArkadeHeroesClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(120));
        while (true)
        {
            try
            {
                return (await client.Heroes.ClaimStartersAsync()).Heroes;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("has not arrived"))
            {
                Assert.True(DateTime.UtcNow < deadline, $"the starter claim fee never cleared: {ex.Message}");
                await Task.Delay(2000);
            }
        }
    }
}
