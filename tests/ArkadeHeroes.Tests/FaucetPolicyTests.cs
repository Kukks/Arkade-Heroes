using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Heroes cost sats, so a player on a test network needs a way to get some — hence a faucet button.
/// The interesting half is where it must NOT appear.
/// </summary>
public class FaucetPolicyTests
{
    /// <summary>
    /// Mutinynet is the network the game is deployed to and the one with a public faucet, so the button
    /// exists there and points at that endpoint.
    /// </summary>
    [Fact]
    public void Mutinynet_HasAFaucet()
    {
        Assert.True(FaucetPolicy.IsAvailableOn("mutinynet"));
        Assert.Equal("https://faucet.mutinynet.arkade.sh/faucet", FaucetPolicy.EndpointFor("mutinynet"));
    }

    /// <summary>
    /// The one that matters. There is no mainnet faucet, so a button there could not work — but the
    /// failure would not be a dead button: pressing it POSTs the player's real receive address to a
    /// third-party service, for nothing. Mainnet must return no endpoint at all.
    /// </summary>
    [Fact]
    public void Mainnet_HasNoFaucet_AndNeverLeaksAnAddress()
    {
        Assert.Null(FaucetPolicy.EndpointFor("mainnet"));
        Assert.False(FaucetPolicy.IsAvailableOn("mainnet"));
    }

    /// <summary>
    /// Regtest runs its own faucet inside the local stack, reached a different way — the public endpoint
    /// is simply the wrong one to call there.
    /// </summary>
    [Fact]
    public void Regtest_DoesNotUseThePublicFaucet()
    {
        Assert.Null(FaucetPolicy.EndpointFor("regtest"));
        Assert.False(FaucetPolicy.IsAvailableOn("regtest"));
    }

    /// <summary>
    /// Anything unrecognised — a typo, a new network, a null from missing config — is treated as having no
    /// faucet. The failure mode of guessing wrong is an address disclosure, so the default has to be "no".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mutinynett")]
    [InlineData("signet")]
    [InlineData("testnet")]
    public void UnknownNetworks_GetNoFaucet(string? network)
    {
        Assert.Null(FaucetPolicy.EndpointFor(network));
        Assert.False(FaucetPolicy.IsAvailableOn(network));
    }

    /// <summary>Config casing and stray whitespace shouldn't decide whether a feature exists.</summary>
    [Theory]
    [InlineData("Mutinynet")]
    [InlineData("MUTINYNET")]
    [InlineData("  mutinynet  ")]
    public void TheNetworkNameIsMatchedLeniently(string network) =>
        Assert.Equal(FaucetPolicy.MutinynetEndpoint, FaucetPolicy.EndpointFor(network));
}
