using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Recruits are the cheap on-ramp: buyable as often as a player will pay, and deliberately the worst heroes
/// in the game. Those two facts hold each other up — unlimited supply is only safe because the supply is
/// junk, and junk is only acceptable because it is the floor rather than the ceiling.
/// </summary>
public class RecruitTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>
    /// The headline change: claiming is no longer one-time. Pay again, get more — and the second batch is
    /// genuinely new heroes, not the first batch handed back.
    /// </summary>
    [Fact]
    public async Task RecruitingIsRepeatable_EachPaidClaimMintsAFreshBatch()
    {
        var (alice, _) = await factory.RegisterAsync($"repeat-{Guid.NewGuid():N}");

        // One purchase each — the helper's default buys a starting pair, which would hide the per-claim size.
        var first = await alice.RecruitAsync(StarterPolicy.HeroCount);
        var second = await alice.RecruitAsync(StarterPolicy.HeroCount);
        var third = await alice.RecruitAsync(StarterPolicy.HeroCount);

        Assert.Equal(StarterPolicy.HeroCount, first.Count);
        Assert.Equal(StarterPolicy.HeroCount, second.Count);

        var everyId = first.Concat(second).Concat(third).Select(h => h.Id).ToList();
        Assert.Equal(everyId.Count, everyId.Distinct().Count());          // no purchase repeats another
        Assert.Equal(StarterPolicy.HeroCount * 3, (await alice.Heroes.MineAsync()).Count);
    }

    /// <summary>
    /// The other side of repeatable: each batch must be BOUGHT. One cleared invoice must not mint heroes
    /// twice — with real sats and an unlimited claim, a reusable invoice would be free hero generation.
    /// </summary>
    [Fact]
    public async Task OnePaidInvoice_BuysExactlyOneBatch()
    {
        var (alice, _) = await factory.RegisterAsync($"once-{Guid.NewGuid():N}");

        var quote = await alice.Heroes.RequestStartersAsync();
        await alice.PayInvoiceAsync(quote.Fee!.InvoiceId);
        Assert.Equal(StarterPolicy.HeroCount, (await alice.Heroes.ClaimStartersAsync()).Heroes.Count);

        // The invoice is spent. Claiming again without buying another must be refused, and mint nothing.
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Heroes.ClaimStartersAsync());
        Assert.Contains("Request your starter heroes first", refused.Message);
        Assert.Equal(StarterPolicy.HeroCount, (await alice.Heroes.MineAsync()).Count);
    }

    /// <summary>A fresh claim after a spent one bills a NEW invoice rather than resurrecting the old id.</summary>
    [Fact]
    public async Task AfterClaiming_TheNextQuoteIsANewInvoice()
    {
        var (alice, _) = await factory.RegisterAsync($"newinv-{Guid.NewGuid():N}");

        var first = await alice.Heroes.RequestStartersAsync();
        await alice.PayInvoiceAsync(first.Fee!.InvoiceId);
        await alice.Heroes.ClaimStartersAsync();

        var second = await alice.Heroes.RequestStartersAsync();
        Assert.NotEqual(first.Fee.InvoiceId, second.Fee!.InvoiceId);
        Assert.Equal(first.FeeSats, second.FeeSats);   // same price, new bill
    }

    /// <summary>
    /// Bottom of the barrel, measured. A recruit expresses no traits at all (gen-0 clears the trait block),
    /// so it scores zero rarity and sits in the lowest tier — however many you buy.
    /// </summary>
    [Fact]
    public async Task RecruitsHaveNoTraits_AndTheLowestRarity()
    {
        var (alice, _) = await factory.RegisterAsync($"junk-{Guid.NewGuid():N}");
        var heroes = (await alice.ClaimStartersAsync()).Concat(await alice.ClaimStartersAsync()).ToList();

        foreach (var hero in heroes)
        {
            var rarity = Rarity.Of(Genome.FromHex(hero.GenomeHex));
            Assert.Empty(rarity.Expressed);
            Assert.Equal(0, rarity.Score);
            Assert.Equal(RarityTier.Common, rarity.Tier);
        }
    }

    /// <summary>
    /// And their stats are capped, which is the part that actually stops farming. Traits were already blank
    /// before recruits were repeatable; stat genes were raw hash bytes, so an unlimited number of claims was
    /// an unlimited number of rolls at a good statline. Every stat and growth gene must sit inside the cap.
    /// </summary>
    [Fact]
    public async Task RecruitStatGenes_AreCappedWellBelowTheFullRange()
    {
        var (alice, _) = await factory.RegisterAsync($"cap-{Guid.NewGuid():N}");
        var heroes = (await alice.ClaimStartersAsync())
            .Concat(await alice.ClaimStartersAsync())
            .Concat(await alice.ClaimStartersAsync())
            .ToList();

        foreach (var hero in heroes)
        {
            var g = Genome.FromHex(hero.GenomeHex);
            for (var i = 0; i <= 4; i++)
                Assert.True(g[i] <= StarterPolicy.RecruitStatCap,
                    $"stat gene {i} was {g[i]}, above the {StarterPolicy.RecruitStatCap} recruit cap");
            for (var i = 8; i <= 12; i++)
                Assert.True(g[i] <= StarterPolicy.RecruitStatCap,
                    $"growth gene {i} was {g[i]}, above the {StarterPolicy.RecruitStatCap} recruit cap");
        }
    }

    /// <summary>
    /// The cap has to actually bind. A cap of 255 would pass the test above while changing nothing, so
    /// check the generator against the uncapped one it replaced: over enough draws, plain gen-0 must break
    /// the ceiling that recruits never do. Deterministic — fixed seeds, no RNG in the assertion.
    /// </summary>
    [Fact]
    public void TheCapBinds_UncappedGen0RoutinelyExceedsIt()
    {
        var uncappedHighRolls = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var entropy = BitConverter.GetBytes(seed);
            var plain = Genome.NewGen0(entropy);
            var recruit = Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap);

            for (var i = 0; i <= 4; i++)
            {
                if (plain[i] > StarterPolicy.RecruitStatCap) uncappedHighRolls++;
                Assert.True(recruit[i] <= StarterPolicy.RecruitStatCap);
            }
        }

        // ~75% of raw bytes exceed a cap of 63, so across 1,000 draws this is overwhelming. If it ever
        // reads zero, the cap stopped being a restriction and these tests stopped meaning anything.
        Assert.True(uncappedHighRolls > 100,
            $"expected plain gen-0 to exceed the cap often; saw {uncappedHighRolls} of 1000");
    }

    /// <summary>Recruits stay verifiable: same entropy in, same genome out, so the mint can be rechecked.</summary>
    [Fact]
    public void RecruitGenomes_AreDeterministic()
    {
        var entropy = "the-same-seed"u8.ToArray();
        Assert.Equal(
            Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap).ToHex(),
            Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap).ToHex());
    }
}
