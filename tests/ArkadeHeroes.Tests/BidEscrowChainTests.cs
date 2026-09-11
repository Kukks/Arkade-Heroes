using ArkadeHeroes.Chain;
using Xunit;

namespace ArkadeHeroes.Tests;

/// <summary>An accepted bid used to bill the WHOLE bid into a treasury invoice, so between acceptance and
/// delivery the treasury held sats owed to the owner. It now receives only its cut, at settlement.</summary>
public class BidEscrowChainTests
{
    private const long Bid = 20_000;
    private const long Fee = 500;

    private static async Task<(InMemoryChainService Chain, string Bidder, string Owner, string HeroAssetId)> ArenaAsync()
    {
        var chain = new InMemoryChainService();
        await chain.RegisterPlayerAddressAsync("bidder", "tark1-bidder");
        await chain.RegisterPlayerAddressAsync("owner", "tark1-owner");
        var mint = await chain.MintHeroAssetAsync("owner",
            new HeroMintData("00", 0, null, null, null, null));
        return (chain, "bidder", "owner", mint.AssetId);
    }

    private static async Task<string> AcceptedAsync(InMemoryChainService chain, string hero, long fee = Fee)
        => await chain.CreateBidEscrowAsync("bid-1", "bidder", "owner", hero, Bid, fee, 1_800_000_000);

    [Fact]
    public async Task FundingABid_LeavesTheBidderAndReachesNoTreasury()
    {
        var (chain, bidder, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        chain.FundBidEscrowFromPlayer(bidder, "bid-1");

        Assert.True(await chain.IsBidEscrowFundedAsync("bid-1"));
        Assert.Equal(InMemoryChainService.FaucetSats - Bid, await chain.GetAddressBalanceSatsAsync(bidder));
        // The whole point: the sats are in the covenant, not in the house.
        Assert.Equal(treasuryBefore, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task SettlingPaysTheOwnerTheBidLessTheCut_AndMovesTheHero()
    {
        var (chain, bidder, owner, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        chain.FundBidEscrowFromPlayer(bidder, "bid-1");
        var ownerBefore = await chain.GetAddressBalanceSatsAsync(owner);
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        chain.SettleBidFromOwner("bid-1");

        Assert.True(await chain.WasBidSettledAsync("bid-1"));
        Assert.Equal(ownerBefore + Bid - Fee, await chain.GetAddressBalanceSatsAsync(owner));
        Assert.Equal(treasuryBefore + Fee, await chain.TreasuryBalanceAsync());
        Assert.True(await chain.VerifyHeroOwnershipAsync(bidder, hero));
    }

    [Fact]
    public async Task AnOwnerWhoNoLongerHoldsTheHero_CannotTakeTheBid()
    {
        // The covenant will not co-sign a settle that fails to deliver, so the simulation must not either.
        var (chain, bidder, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        chain.FundBidEscrowFromPlayer(bidder, "bid-1");
        chain.TransferAssetFromPlayer("owner", "bidder", hero);

        Assert.Throws<InvalidOperationException>(() => chain.SettleBidFromOwner("bid-1"));
        Assert.False(await chain.WasBidSettledAsync("bid-1"));
    }

    [Fact]
    public async Task AnUnfundedBid_CannotBeSettled()
    {
        var (chain, _, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        Assert.Throws<InvalidOperationException>(() => chain.SettleBidFromOwner("bid-1"));
    }

    [Fact]
    public async Task ABidSettlesExactlyOnce()
    {
        var (chain, bidder, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        chain.FundBidEscrowFromPlayer(bidder, "bid-1");
        chain.SettleBidFromOwner("bid-1");

        Assert.Throws<InvalidOperationException>(() => chain.SettleBidFromOwner("bid-1"));
    }

    [Fact]
    public async Task ABidIsFundedOnce_NeverTwice()
    {
        // A second deduction would bill a player for a bid they already made.
        var (chain, bidder, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        chain.FundBidEscrowFromPlayer(bidder, "bid-1");
        var after = await chain.GetAddressBalanceSatsAsync(bidder);

        Assert.Throws<InvalidOperationException>(() => chain.FundBidEscrowFromPlayer(bidder, "bid-1"));
        Assert.Equal(after, await chain.GetAddressBalanceSatsAsync(bidder));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1_000, 1_000)]   // a cut that swallows the whole bid
    [InlineData(1_000, -1)]
    public async Task ABidTheCovenantCouldNotHonour_IsRefusedBeforeItIsStored(long bid, long fee)
    {
        var (chain, _, _, hero) = await ArenaAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chain.CreateBidEscrowAsync("bid-bad", "bidder", "owner", hero, bid, fee, 1_800_000_000));
        Assert.Null(await chain.GetBidEscrowParamsAsync("bid-bad"));
    }

    [Fact]
    public async Task ConcurrentSettles_PayTheOwnerOnce()
    {
        var (chain, bidder, owner, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);
        chain.FundBidEscrowFromPlayer(bidder, "bid-1");
        var before = await chain.GetAddressBalanceSatsAsync(owner);

        var wins = 0;
        var start = new Barrier(8);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            try { chain.SettleBidFromOwner("bid-1"); Interlocked.Increment(ref wins); }
            catch (InvalidOperationException) { }
        })));

        Assert.Equal(1, wins);
        Assert.Equal(before + Bid - Fee, await chain.GetAddressBalanceSatsAsync(owner));
    }

    [Fact]
    public async Task TheParamsRebuildTheCovenantTheBidderReclaimsWith()
    {
        var (chain, _, _, hero) = await ArenaAsync();
        await AcceptedAsync(chain, hero);

        var p = await chain.GetBidEscrowParamsAsync("bid-1");
        Assert.NotNull(p);
        Assert.Equal(hero, p!.HeroAssetId);
        Assert.Equal(Bid, p.BidSats);
        Assert.Equal(Fee, p.FeeSats);
        Assert.Equal(1_800_000_000, p.RefundAfterUnixSeconds);
        Assert.NotEqual(p.BidderAddress, p.OwnerAddress);
    }
}
