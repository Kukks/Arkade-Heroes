namespace ArkadeHeroes.Shared;

/// <summary>
/// Where — and whether — the wallet may ask a public faucet for test coins.
///
/// <para>Heroes cost sats now, so a new player on a test network needs a way to get some without going
/// and finding one. Mutinynet has a public faucet and the game is deployed there, so the wallet can offer
/// it as a button.</para>
///
/// <para>The gate matters more than the button. On <b>mainnet there is no faucet</b>, and offering one
/// would do something worse than fail: it would POST the player's real receive address to a third-party
/// service to no purpose. On regtest the faucet is the local stack's own, reached a different way, so the
/// public endpoint is wrong there too. Both are off — the availability decision lives here, as data, so it
/// can be tested without a browser.</para>
/// </summary>
public static class FaucetPolicy
{
    /// <summary>Mutinynet's public Arkade faucet. Takes <c>{ address, amount }</c>, returns 200 on success.</summary>
    public const string MutinynetEndpoint = "https://faucet.mutinynet.arkade.sh/faucet";

    /// <summary>
    /// The faucet endpoint for a network, or null when the wallet must not offer one. Unknown names are
    /// treated as "no faucet": a typo should cost a button, never an address disclosure.
    /// </summary>
    public static string? EndpointFor(string? network) =>
        (network ?? "").Trim().ToLowerInvariant() switch
        {
            "mutinynet" => MutinynetEndpoint,
            _ => null,
        };

    /// <summary>True when this network has a faucet the wallet may offer.</summary>
    public static bool IsAvailableOn(string? network) => EndpointFor(network) is not null;
}
