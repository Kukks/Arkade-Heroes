using ArkadeHeroes.Chain.Covenants;
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
public class GameSession(ArkadeHeroesClient api, GameWallet wallet, WalletState state, IServiceProvider services)
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

    /// <summary>
    /// The game-first "start playing" entry — keeps the wallet out of the player's way. Auto-provisions
    /// a wallet if there isn't one (flagging a deferred recovery-phrase backup), signs in with it under
    /// the chosen arena name (resuming an already-registered wallet, else registering), and claims the
    /// starter heroes. The player only ever picks a name and plays. Returns the roster.
    /// </summary>
    public async Task<IReadOnlyList<HeroDto>> StartPlayingAsync(string name)
    {
        // 1. Ensure a wallet exists — auto-create silently and flag that its recovery phrase still
        //    needs backing up (non-custodial: the key exists, the backup is just deferred off the path).
        if (await wallet.GetActiveWalletAsync() is null)
        {
            var (created, _) = await wallet.CreateAsync();
            var address = await wallet.GetReceiveAddressAsync(created.Id);
            state.SetActiveWallet(created.Id, address);
            state.UpdateBalance(await wallet.GetBalanceAsync(created.Id));
            state.SetBackupPending(true);
        }

        // 2. Sign in: resume this wallet if it's already a registered player, else register the name.
        if (!state.IsSignedIn)
        {
            await ResumeAsync();
            if (!state.IsSignedIn)
                await RegisterAsync(name);
        }

        // 3. Claim starters so the player lands already owning a roster (idempotent; a returning
        //    player who already claimed just gets their current roster back).
        if (state.Player is { StarterClaimed: false })
            return await ClaimStartersAsync();
        return await api.Heroes.MineAsync();
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
    public async Task<HeroDto> BreedAsync(string parentAId, string parentBId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        // 1. Commit (covenant mode) — the server returns the escrow address + fee.
        onProgress?.Invoke("Sealing the breeding covenant…");
        var commit = await api.Breeding.CommitAsync(new BreedCommitRequest(parentAId, parentBId, "covenant"));
        if (string.IsNullOrEmpty(commit.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // 2. Deposit both parents + the fee into the escrow (plain sends to one opaque address),
        //    each waiting for its spend to settle before the next so they don't contend for one coin.
        var heroA = await api.Heroes.GetAsync(parentAId);
        var heroB = await api.Heroes.GetAsync(parentBId);
        onProgress?.Invoke("Escrowing the first parent…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroA.AssetId ?? heroA.Id, 0);
        onProgress?.Invoke("Escrowing the second parent…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroB.AssetId ?? heroB.Id, 0);
        if (commit.EscrowFeeSats > 0)
        {
            onProgress?.Invoke("Paying the breeding fee…");
            await DepositAndSettleAsync(w.Id, commit.EscrowAddress, null, commit.EscrowFeeSats);
        }

        // 3. Reveal — retry while the deposits settle into arkd's indexer (the funding gate).
        onProgress?.Invoke("Minting the child under species control…");
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
                onProgress?.Invoke($"Escrow still settling — retrying ({attempt + 1}/20)…");
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
    public async Task<HeroDto> MergeAsync(string baseId, string sacrificeId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Sealing the fusion covenant…");
        var commit = await api.Merge.CommitAsync(new MergeCommitRequest(baseId, sacrificeId, "covenant"));
        if (string.IsNullOrEmpty(commit.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        var heroBase = await api.Heroes.GetAsync(baseId);
        var heroSac = await api.Heroes.GetAsync(sacrificeId);
        onProgress?.Invoke("Escrowing the base hero…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroBase.AssetId ?? heroBase.Id, 0);
        onProgress?.Invoke("Escrowing the sacrifice…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroSac.AssetId ?? heroSac.Id, 0);
        if (commit.FeeSats > 0)
        {
            onProgress?.Invoke("Paying the fusion fee…");
            await DepositAndSettleAsync(w.Id, commit.EscrowAddress, null, commit.FeeSats);
        }

        onProgress?.Invoke("Forging the fused hero…");
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
                onProgress?.Invoke($"Escrow still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new GameWalletException(
            $"The escrow deposits haven't settled yet — try merging again in a moment. ({last?.Message})");
    }

    /// <summary>
    /// List one of the player's heroes for sale under covenant enforcement, from the browser wallet:
    /// create the offer, then deposit the hero (one asset unit) into the server-returned offer address
    /// — a single non-custodial send. Once the deposit is observed on-chain the offer rests
    /// <c>active</c> on the market for any buyer to fulfil trustlessly. Returns the resting offer.
    /// </summary>
    public async Task<OfferDto> ListHeroAsync(string heroId, long askSats, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        // 1. Create the offer — the server returns the offer address + the hero's asset id.
        onProgress?.Invoke("Drafting the sale covenant…");
        var offer = await api.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroId, askSats));

        // 2. Deposit the hero (one asset unit) into the offer address, waiting for the spend to settle.
        onProgress?.Invoke("Escrowing your hero into the offer…");
        await DepositAndSettleAsync(w.Id, offer.OfferAddress, offer.ItemAssetId, 0);

        // 3. Poll until the server observes the funded offer resting active on the market.
        onProgress?.Invoke("Waiting for the offer to rest on the market…");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        var resting = await api.Offers.GetAsync(offer.OfferId);
        while (resting.Status != "active" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(2500);
            resting = await api.Offers.GetAsync(offer.OfferId);
        }
        return resting;
    }

    /// <summary>
    /// Buy a resting hero offer, entirely from the browser wallet: rebuild the offer covenant
    /// locally and fulfil it — the buyer pays the ask straight to the seller and the covenant
    /// hands over the hero (the emulator co-signs only if the seller is paid exactly the ask).
    /// Then claim game-side ownership. Non-custodial: the server never touches the buyer's key.
    /// Returns the bought hero.
    /// </summary>
    public async Task<HeroDto> BuyHeroAsync(string offerId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Rebuilding the offer covenant…");
        var offer = await api.Offers.ParamsAsync(offerId);
        var info = await api.Chain.InfoAsync();
        if (string.IsNullOrEmpty(info.EmulatorUri))
            throw new GameWalletException("This arena isn't in covenant mode (no emulator advertised).");

        // Deliver the hero to the player's REGISTERED address — the one the server verifies for
        // the claim. A fresh GetReceiveAddressAsync advances once an address is funded, so it
        // would land the hero on a derivation the server doesn't check (the wallet still controls
        // it, but the game-side claim would never see it). Fall back only if somehow unsigned-in.
        var address = state.Player?.ArkadeAddress ?? await wallet.GetReceiveAddressAsync(w.Id);

        // Fulfil the offer covenant from THIS wallet's NArk services (the browser's DI graph):
        // pay the ask, take the hero. Trustless — the contract is rebuilt from the public params.
        onProgress?.Invoke("Paying the seller & taking the hero…");
        await OfferFulfillFlow.FulfillAsync(services, w.Id, address, new Uri(info.EmulatorUri), offer);

        // Claim game-side ownership — retry while the hero lands + the server observes it on-chain.
        onProgress?.Invoke("Claiming ownership…");
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Offers.ClaimHeroAsync(offerId)).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("chain does not show", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Waiting for the hero to sync ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new GameWalletException(
            $"Paid on-chain, but the hero hasn't synced for the claim yet — try again in a moment. ({last?.Message})");
    }

    /// <summary>
    /// Buy a catalog item from the browser wallet: ask the server for a fee invoice, pay it with a
    /// plain non-custodial send (the same deposit-and-settle primitive breed/merge use), then claim —
    /// the server verifies payment on-chain and delivers a fungible item-asset unit to the player.
    /// Returns the delivered asset id + units now held. Retries the claim while the payment settles.
    /// </summary>
    public async Task<ClaimItemResponse> BuyItemAsync(string itemId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Requesting a payment invoice…");
        var invoice = (await api.Items.BuyAsync(itemId)).Invoice;
        if (invoice.AmountSats > 0)
        {
            onProgress?.Invoke("Paying for the item…");
            await DepositAndSettleAsync(w.Id, invoice.PayToAddress, null, invoice.AmountSats);
        }

        onProgress?.Invoke("Delivering your item…");
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return await api.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("not been paid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Waiting for payment to settle ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new GameWalletException(
            $"Paid on-chain, but the payment hasn't settled for the claim yet — try again in a moment. ({last?.Message})");
    }

    /// <summary>Equip an owned item onto one of the player's heroes (server enforces ownership + slot).</summary>
    public async Task<HeroDto> EquipAsync(string heroId, string itemId) =>
        (await api.Heroes.EquipAsync(heroId, new EquipRequest(itemId))).Hero;

    /// <summary>Clear a hero's equipment slot (Weapon/Armor/Trinket), returning the item to the player.</summary>
    public async Task<HeroDto> UnequipAsync(string heroId, string slot) =>
        (await api.Heroes.UnequipAsync(heroId, new UnequipRequest(slot))).Hero;

    // One escrow deposit (a hero asset when assetId is set, else sats), then wait for the wallet's
    // coins to re-settle before returning — so the next deposit in the sequence doesn't contend for
    // the just-spent (arkd-locked) BTC coin or race its change syncing back in.
    private async Task DepositAndSettleAsync(string walletId, string escrow, string? assetId, long sats)
    {
        var before = await wallet.SpendableBtcOutpointsAsync(walletId);
        if (assetId is not null)
            await wallet.SendAssetAsync(walletId, escrow, assetId, 1);
        else
            await wallet.SendSatsAsync(walletId, escrow, sats);
        await wallet.WaitForSpendToSettleAsync(walletId, before, TimeSpan.FromSeconds(60));
    }

    private static string RandomNonce() =>
        Convert.ToHexString(RandomUtils.GetBytes(16)).ToLowerInvariant();
}
