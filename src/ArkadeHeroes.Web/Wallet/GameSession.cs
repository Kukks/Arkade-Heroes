using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using NBitcoin;

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

    /// <summary>
    /// Breed two heroes under covenant enforcement, entirely from the browser wallet: commit,
    /// deposit both parents + the fee into the server-returned escrow address (three plain
    /// non-custodial sends — the covenant enforcement lives at the address, server-side), then
    /// reveal. The server assembles the covenant mint (child under species to the player).
    /// Returns the child hero.
    /// </summary>
    public async Task<HeroDto> BreedAsync(string parentAId, string parentBId)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        // 1. Commit (covenant mode) — the server returns the escrow address + fee.
        var commit = await api.Breeding.CommitAsync(new BreedCommitRequest(parentAId, parentBId, "covenant"));
        if (string.IsNullOrEmpty(commit.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // 2. Deposit both parents + the fee into the escrow (plain sends to one opaque address).
        //    Space the sends: each spends the wallet's BTC coin and produces change, and the
        //    next send must wait for that change to sync in — otherwise it contends for the
        //    just-spent (now arkd-locked) coin. A pause between sends lets the wallet catch up.
        var heroA = await api.Heroes.GetAsync(parentAId);
        var heroB = await api.Heroes.GetAsync(parentBId);
        await wallet.SendAssetAsync(w.Id, commit.EscrowAddress, heroA.AssetId ?? heroA.Id, 1);
        await Task.Delay(10000);
        await wallet.SendAssetAsync(w.Id, commit.EscrowAddress, heroB.AssetId ?? heroB.Id, 1);
        await Task.Delay(10000);
        if (commit.EscrowFeeSats > 0)
        {
            await wallet.SendSatsAsync(w.Id, commit.EscrowAddress, commit.EscrowFeeSats);
            await Task.Delay(10000);
        }

        // 3. Reveal — retry while the deposits settle into arkd's indexer (the funding gate).
        var nonce = RandomNonce();
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest(nonce))).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("breed escrow", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                await Task.Delay(3000);
            }
        }
        throw new GameWalletException(
            $"The escrow deposits haven't settled yet — try revealing again in a moment. ({last?.Message})");
    }

    /// <summary>
    /// Merge (fuse) two heroes under covenant enforcement from the browser wallet: commit, deposit
    /// the base + sacrifice + fee into the escrow (same plain-send pattern as breed), then reveal.
    /// The server burns both inputs and mints ONE trait-concentrated fused hero to the player.
    /// Returns the fused hero.
    /// </summary>
    public async Task<HeroDto> MergeAsync(string baseId, string sacrificeId)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        var commit = await api.Merge.CommitAsync(new MergeCommitRequest(baseId, sacrificeId, "covenant"));
        if (string.IsNullOrEmpty(commit.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        var heroBase = await api.Heroes.GetAsync(baseId);
        var heroSac = await api.Heroes.GetAsync(sacrificeId);
        await wallet.SendAssetAsync(w.Id, commit.EscrowAddress, heroBase.AssetId ?? heroBase.Id, 1);
        await Task.Delay(10000);
        await wallet.SendAssetAsync(w.Id, commit.EscrowAddress, heroSac.AssetId ?? heroSac.Id, 1);
        await Task.Delay(10000);
        if (commit.FeeSats > 0)
        {
            await wallet.SendSatsAsync(w.Id, commit.EscrowAddress, commit.FeeSats);
            await Task.Delay(10000);
        }

        var nonce = RandomNonce();
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest(nonce))).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("merge escrow", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                await Task.Delay(3000);
            }
        }
        throw new GameWalletException(
            $"The escrow deposits haven't settled yet — try merging again in a moment. ({last?.Message})");
    }

    private static string RandomNonce() =>
        Convert.ToHexString(RandomUtils.GetBytes(16)).ToLowerInvariant();
}
