using ArkadeHeroes.Chain;

namespace ArkadeHeroes.Tests;

/// <summary>The treasury-balance read (the season-pot coverage check) reflects credits — InMemory.</summary>
public class TreasuryBalanceTests
{
    [Fact]
    public async Task FundTreasury_IsReflectedInBalance()
    {
        var chain = new InMemoryChainService();
        Assert.Equal(0, await chain.TreasuryBalanceAsync());
        chain.FundTreasury(7_500);
        Assert.Equal(7_500, await chain.TreasuryBalanceAsync());
    }
}
