using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Starter heroes are bought, not given — including the first one.
///
/// <para>They used to be free, which meant anyone able to generate a keypair could mint real assets, and a
/// keypair costs nothing: the giveaway scaled with the attacker rather than with the playerbase. Charging
/// for them closes that, and the price is not a new number — a claimed hero costs exactly what a bred one
/// costs, so the cheapest way to obtain a hero is the same however you obtain it.</para>
/// </summary>
public class StarterClaimFeeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// The invariant, measured rather than restated: the per-hero claim price equals the fee an actual
    /// first breed is billed. Deriving one from the other is what keeps this true — a second, independent
    /// config knob would let the two drift, and this test is what would notice.
    ///
    /// <para>It compares against a REAL breed invoice, not against the same expression the server used, so
    /// pinning the claim fee to a literal fails here even when the literal happens to match today.</para>
    /// </summary>
    [Fact]
    public async Task ClaimingAHero_CostsWhatBreedingOneCosts()
    {
        var (alice, _) = await factory.RegisterAsync($"fee-{Guid.NewGuid():N}");

        var quote = await alice.Heroes.RequestStartersAsync();
        Assert.NotNull(quote.Fee);
        var perHero = quote.FeeSats / quote.HeroCount;

        // Pay and claim, then buy a second — a breed quote needs two unbred parents, and a recruit is a
        // single hero, so getting a pair means purchasing twice.
        await alice.PayInvoiceAsync(quote.Fee!.InvoiceId);
        var heroes = (await alice.Heroes.ClaimStartersAsync()).Heroes.ToList();
        Assert.Equal(quote.HeroCount, heroes.Count);
        heroes.AddRange(await alice.RecruitAsync(StarterPolicy.HeroCount));

        // A first breed — neither parent has bred, so this is the floor price of a new hero.
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        Assert.Equal(commit.Invoice!.AmountSats, perHero);
        Assert.Equal(commit.Invoice.AmountSats * quote.HeroCount, quote.FeeSats);
    }

    /// <summary>
    /// The gate itself: quoting is not buying. Without payment the claim must be refused AND leave the
    /// player hero-less — a mint that happened anyway would be the whole feature failing silently.
    /// </summary>
    [Fact]
    public async Task WithoutPayingTheFee_NoHeroesAreMinted()
    {
        var (alice, _) = await factory.RegisterAsync($"unpaid-{Guid.NewGuid():N}");
        await alice.Heroes.RequestStartersAsync();   // quoted, deliberately unpaid

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Heroes.ClaimStartersAsync());
        Assert.Contains("has not arrived", refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await alice.Heroes.MineAsync());
        Assert.False((await alice.Players.MeAsync()).StarterClaimed);
    }

    /// <summary>
    /// Claiming without ever asking for a price is refused too — otherwise the fee would be optional for
    /// anyone calling the API directly rather than through the UI.
    /// </summary>
    [Fact]
    public async Task ClaimingWithoutRequestingAQuote_IsRefused()
    {
        var (alice, _) = await factory.RegisterAsync($"noquote-{Guid.NewGuid():N}");

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Heroes.ClaimStartersAsync());
        Assert.Contains("Request your starter heroes first", refused.Message);
        Assert.Empty(await alice.Heroes.MineAsync());
    }

    /// <summary>
    /// Asking twice must not bill twice. A player who loses the first response — a closed tab, a failed
    /// mint, a server restart — may already have paid; handing them a fresh invoice would take the fee
    /// twice for one set of heroes. Real sats, so this is a money bug, not a UX wart.
    /// </summary>
    [Fact]
    public async Task RequestingTwice_ReusesTheSameInvoice_AndDoesNotBillTwice()
    {
        var (alice, _) = await factory.RegisterAsync($"rebill-{Guid.NewGuid():N}");

        var first = await alice.Heroes.RequestStartersAsync();
        var second = await alice.Heroes.RequestStartersAsync();

        Assert.Equal(first.Fee!.InvoiceId, second.Fee!.InvoiceId);
        Assert.Equal(first.Fee.PayToAddress, second.Fee.PayToAddress);
        Assert.Equal(first.FeeSats, second.FeeSats);

        // And paying that one invoice is enough to claim — the second quote didn't raise the bill.
        await alice.PayInvoiceAsync(second.Fee.InvoiceId);
        Assert.Equal(first.HeroCount, (await alice.Heroes.ClaimStartersAsync()).Heroes.Count);
    }

    /// <summary>
    /// An operator who sets the breed fee to zero is running a free server, and the claim follows it down
    /// to free as well — the two prices move together in both directions, which is what "the same as
    /// breeding" has to mean if it means anything.
    /// </summary>
    [Fact]
    public async Task OnAFreeServer_TheClaimIsFreeToo()
    {
        using var free = factory.WithWebHostBuilder(b => b.UseSetting("Game:BreedingFeeSats", "0"));
        var (alice, _) = await free.RegisterAsync($"free-{Guid.NewGuid():N}");

        var quote = await alice.Heroes.RequestStartersAsync();
        Assert.Equal(0, quote.FeeSats);
        Assert.Null(quote.Fee);

        Assert.Equal(quote.HeroCount, (await alice.Heroes.ClaimStartersAsync()).Heroes.Count);
    }

    /// <summary>
    /// The E2E suite must reach a starter hero through the helper that PAYS for it, and nowhere else.
    ///
    /// <para>This is the guard for how the claim fee actually broke things. When the claim started costing
    /// sats, the unit suite's helper was migrated to quote → pay → claim and the E2E suite's was not — two
    /// suites with parallel helpers, one of them updated. Nothing failed, because E2E runs only nightly and
    /// on manual dispatch: the contract change landed green and the gate stayed red for 31 commits before
    /// anyone dispatched it.</para>
    ///
    /// <para>So the check lives HERE, in the suite that runs on every PR, and reads the other suite's source
    /// rather than its behaviour — that is the only way one assembly can hold the other to this without
    /// referencing it, and it is enough: a test that calls the endpoint directly cannot have paid, because
    /// paying in NArk mode means moving real sats from a real wallet, which is precisely what the helper is
    /// for. A future change to the claim contract now breaks a PR-time test instead of a nightly one.</para>
    /// </summary>
    [Fact]
    public void TheE2ESuite_BuysItsStartersThroughThePayingHelper_AndNeverCallsTheEndpointDirectly()
    {
        var e2e = Path.Combine(FindRepoRoot(), "tests", "ArkadeHeroes.Tests.E2E");
        const string helper = "StarterPurchaseHelpers.cs";

        // The helper itself walks the full sequence — quote, pay from the wallet, then claim.
        var helperSource = File.ReadAllText(Path.Combine(e2e, helper));
        var quote = helperSource.IndexOf("RequestStartersAsync", StringComparison.Ordinal);
        var pay = helperSource.IndexOf("SendAsync", StringComparison.Ordinal);
        var claim = helperSource.IndexOf("ClaimStartersAsync", StringComparison.Ordinal);
        Assert.True(quote >= 0 && pay > quote && claim > pay,
            $"{helper} must quote, then pay from the player's wallet, then claim — in that order.");

        // And every other E2E file goes through it. A direct call is a test claiming for free again.
        var offenders = Directory.EnumerateFiles(e2e, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != helper)
            .Where(f => File.ReadAllText(f).Contains("Heroes.ClaimStartersAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"E2E tests must buy starters via {helper} (RecruitAsync), not call the claim endpoint directly — "
            + $"starters cost sats and there is no dev pay-invoice facade in NArk mode. Offenders: "
            + string.Join(", ", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }
}
