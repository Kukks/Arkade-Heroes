using System.Net.Http.Json;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>Which Ark network this build talks to, as a plain name the UI can branch on.</summary>
public sealed record ArkNetworkInfo(string Name);

/// <summary>
/// Asks the network's public faucet to send the player's own wallet some test coins.
///
/// <para>Non-custodial throughout: the faucet pays the player's receive address directly, so the sats are
/// theirs the moment they land and nothing passes through the game server. Whether a faucet may be offered
/// at all is <see cref="FaucetPolicy"/>'s decision, not this class's — see the note there about mainnet.</para>
/// </summary>
public class FaucetService(HttpClient http, ArkNetworkInfo network, GameWallet wallet, WalletState state)
{
    /// <summary>How much to ask for. Enough to buy a starter claim and still have change for a first breed.</summary>
    public const long DefaultRequestSats = 10_000;

    /// <summary>True when this network has a faucet worth showing a button for.</summary>
    public bool Available => FaucetPolicy.IsAvailableOn(network.Name);

    /// <summary>
    /// Requests coins to the active wallet's receive address. Returns the amount asked for; throws with the
    /// faucet's own message when it refuses, because "try again" is useless advice when the real answer is
    /// a rate limit or a cap.
    /// </summary>
    public async Task<long> RequestAsync(long sats = DefaultRequestSats, CancellationToken ct = default)
    {
        if (FaucetPolicy.EndpointFor(network.Name) is not { } endpoint)
            throw new InvalidOperationException($"There is no faucet on {network.Name}.");

        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");
        var address = await wallet.GetReceiveAddressAsync(w.Id);

        using var response = await http.PostAsJsonAsync(
            endpoint, new { address, amount = sats }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            throw new InvalidOperationException(body.Length > 0
                ? $"The faucet refused: {body}"
                : $"The faucet refused ({(int)response.StatusCode}).");
        }

        // The coins arrive as a VTXO the background sync will notice; nudge the HUD so the balance
        // doesn't sit stale while the player waits and wonders whether the button worked.
        state.NotifyChanged();
        return sats;
    }
}
