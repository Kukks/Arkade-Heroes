using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

public class NameRegistryTests
{
    [Fact]
    public void Validate_AcceptsAWellFormedName_AndTrims()
    {
        Assert.Null(NameRegistry.Validate("  Sir Reginald III  ", out var normalized));
        Assert.Equal("Sir Reginald III", normalized);
    }

    [Theory]
    [InlineData("")]                                    // empty
    [InlineData("ab")]                                  // too short
    [InlineData("this name is far too long to claim")]  // too long
    [InlineData("bad@name")]                            // illegal character
    [InlineData("double  space")]                       // double space
    public void Validate_RejectsIllegalNames(string bad) => Assert.NotNull(NameRegistry.Validate(bad, out _));
}

/// <summary>
/// The unique-name registry: a player pays a flat treasury fee to claim a custom, globally-unique name
/// for a hero. Two-phase (request → pay → confirm), mirroring the breed fee; the name is not applied
/// until the fee clears, and no two heroes may hold the same name.
/// </summary>
public class HeroRenameTests
{
    const long Fee = 500;   // GameOptions.HeroRenameFeeSats default

    [Fact]
    public async Task Rename_NotAppliedUntilFeePaid_ThenTreasuryCaptured()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (player, _) = await factory.RegisterAsync("RN-Owner");
        var hero = (await player.ClaimStartersAsync())[0];

        var treasuryBefore = await chain.TreasuryBalanceAsync();

        var resp = await player.Heroes.RequestRenameAsync(hero.Id, new RenameHeroRequest("Ser Percival"));
        Assert.Equal(Fee, resp.FeeSats);
        Assert.NotNull(resp.Fee);

        // Not applied until the fee clears.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => player.Heroes.ConfirmRenameAsync(hero.Id));
        Assert.NotEqual("Ser Percival", (await player.Heroes.GetAsync(hero.Id)).Name);

        // Pay → confirm → the name is applied and the treasury is credited by exactly the fee.
        await player.Dev.PayInvoiceAsync(new { resp.Fee!.InvoiceId });
        var renamed = await player.Heroes.ConfirmRenameAsync(hero.Id);
        Assert.Equal("Ser Percival", renamed.Name);
        Assert.Equal(treasuryBefore + Fee, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task Rename_RejectsANameHeldByAnotherHero()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("RN-Dupe");
        var heroes = await player.ClaimStartersAsync();

        // Claim a name on the first hero.
        var first = await player.Heroes.RequestRenameAsync(heroes[0].Id, new RenameHeroRequest("Highlander"));
        await player.Dev.PayInvoiceAsync(new { first.Fee!.InvoiceId });
        await player.Heroes.ConfirmRenameAsync(heroes[0].Id);

        // The second hero cannot claim the same name — there can be only one.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => player.Heroes.RequestRenameAsync(heroes[1].Id, new RenameHeroRequest("Highlander")));
    }

    /// <summary>
    /// Losing the apply-time race must not cost the player the fee. Uniqueness is checked twice — once
    /// when the name is requested and again when it is confirmed — so two heroes can both hold a pending
    /// claim on the same name and whoever confirms second is refused. That second player has already PAID
    /// by then, and the refusal leaves the rename session standing, so asking for a different name used to
    /// mint a fresh invoice and bill them all over again for a race they did not lose through any fault.
    ///
    /// One paid fee buys one APPLIED rename, however many names it takes to find a free one.
    /// </summary>
    [Fact]
    public async Task Rename_LosingTheApplyTimeRace_DoesNotChargeTheFeeTwice()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("RN-Race");
        var heroes = await player.ClaimStartersAsync();

        var balanceBefore = (await player.Players.MeAsync()).BalanceSats;

        // Both heroes stake a claim on the same name and both fees clear — legal, because uniqueness is
        // only settled at confirm time.
        var slow = await player.Heroes.RequestRenameAsync(heroes[0].Id, new RenameHeroRequest("Highlander"));
        await player.Dev.PayInvoiceAsync(new { slow.Fee!.InvoiceId });
        var quick = await player.Heroes.RequestRenameAsync(heroes[1].Id, new RenameHeroRequest("Highlander"));
        await player.Dev.PayInvoiceAsync(new { quick.Fee!.InvoiceId });

        // The quicker hero takes the name; the slower one is refused, fee already spent.
        await player.Heroes.ConfirmRenameAsync(heroes[1].Id);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => player.Heroes.ConfirmRenameAsync(heroes[0].Id));

        var spentSoFar = balanceBefore - (await player.Players.MeAsync()).BalanceSats;

        // Picking another name reuses the fee already paid — no second invoice to settle.
        var retry = await player.Heroes.RequestRenameAsync(heroes[0].Id, new RenameHeroRequest("Wanderer"));
        Assert.Null(retry.Fee);

        var renamed = await player.Heroes.ConfirmRenameAsync(heroes[0].Id);
        Assert.Equal("Wanderer", renamed.Name);

        // Two heroes renamed, two fees — not three.
        Assert.Equal(spentSoFar, balanceBefore - (await player.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task Config_PublishesRenameFee_ForPreDisplay()
    {
        // The hero page previews the rename fee from GET /api/chain/info before the owner claims a name.
        using var factory = new WebApplicationFactory<Program>();
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var info = await client.Chain.InfoAsync();
        Assert.Equal(Fee, info.Config?.HeroRenameFeeSats);
    }
}
