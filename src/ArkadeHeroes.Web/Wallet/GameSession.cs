using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// Bridges the in-browser wallet to a game identity: "sign in with your wallet". The wallet
/// proves control of its stable login key (BIP340 over a server nonce) to register or resume
/// a player — the server never holds a key. Scoped so it can use the request-scoped SDK client
/// (whose bearer token, set on register/login, persists for the app in WASM's single scope).
/// </summary>
public class GameSession(ArkadeHeroesClient api, GameWallet wallet, WalletState state)
{
    /// <summary>
    /// Silently resume the player this wallet is registered as (sign a fresh challenge and log in).
    /// No-op if there's no wallet, we're already signed in, or the login key isn't registered yet
    /// (a brand-new wallet) — the caller then offers Register.
    /// </summary>
    public async Task ResumeAsync()
    {
        var w = await wallet.GetActiveWalletAsync();
        if (w is null || state.IsSignedIn) return;
        try
        {
            state.SetPlayer(await LoginAsync(w.Id));
        }
        catch (ArkadeHeroesApiException)
        {
            // Login key not registered (new wallet) — stay signed out; Register is offered.
        }
    }

    /// <summary>
    /// Register a new player: bind this wallet's receive address + login key (with proof-of-possession)
    /// to the chosen name, and sign in.
    /// </summary>
    public async Task<PlayerDto> RegisterAsync(string name)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");
        var address = await wallet.GetReceiveAddressAsync(w.Id);
        var challenge = await api.Players.LoginChallengeAsync();
        var signed = await wallet.SignLoginAsync(w.Id, challenge.NonceHex)
            ?? throw new GameWalletException("This wallet can't sign in (no recovery phrase).");
        var player = await api.Players.RegisterAsync(new RegisterPlayerRequest(
            name.Trim(), address, signed.PubKeyHex, challenge.NonceHex, signed.SignatureHex));
        state.SetPlayer(player);
        return player;
    }

    /// <summary>Claim the one-time starter heroes for the signed-in player.</summary>
    public async Task<IReadOnlyList<HeroDto>> ClaimStartersAsync()
    {
        var res = await api.Heroes.ClaimStartersAsync();
        // StarterClaimed has flipped — refresh the player so the UI hides the claim button.
        state.SetPlayer(await api.Players.MeAsync());
        return res.Heroes;
    }

    private async Task<PlayerDto> LoginAsync(string walletId)
    {
        var challenge = await api.Players.LoginChallengeAsync();
        var signed = await wallet.SignLoginAsync(walletId, challenge.NonceHex)
            ?? throw new GameWalletException("This wallet can't sign in (no recovery phrase).");
        return await api.Players.LoginAsync(new LoginRequest(signed.PubKeyHex, challenge.NonceHex, signed.SignatureHex));
    }
}
