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
public class GameSession(ArkadeHeroesClient api, GameWallet wallet, WalletState state, TermsState terms, IServiceProvider services, HttpClient http)
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
    /// to the chosen name, and sign in. The Terms version the player accepted at the gate rides along, so
    /// the acceptance is recorded server-side in the SAME call that creates the player — there is never a
    /// player row with no acceptance on file when one was actually given.
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
            name.Trim(), address, signed.PubKeyHex, challenge.NonceHex, signed.SignatureHex,
            terms.VersionToRecord));
        state.SetPlayer(player);
        return player;
    }

    /// <summary>
    /// Buy the one-time starter heroes for the signed-in player: quote the fee, pay it from the player's
    /// own wallet, then claim. A hero costs what breeding one costs — including the first — so this is a
    /// real payment, not a formality, and the heroes only exist once it lands.
    /// </summary>
    public async Task<IReadOnlyList<HeroDto>> ClaimStartersAsync(Action<string>? onProgress = null)
    {
        var quote = await api.Heroes.RequestStartersAsync();
        if (quote.Fee is { AmountSats: > 0 } fee)
        {
            var w = await wallet.GetActiveWalletAsync()
                ?? throw new GameWalletException("Create a wallet first.");
            onProgress?.Invoke($"Paying the {fee.AmountSats} sat claim fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        onProgress?.Invoke("Summoning your heroes…");
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
            // The terms were accepted a moment ago, before this wallet existed — there was no key to cache
            // the answer under at the time. Attach it now so this wallet isn't re-asked on the next load.
            terms.AttachToWallet(created.Id);
        }

        // 2. Sign in: resume this wallet if it's already a registered player, else register the name.
        if (!state.IsSignedIn)
        {
            await ResumeAsync();
            if (!state.IsSignedIn)
                await RegisterAsync(name);
        }

        // 3. Now that a player record exists, the SERVER's acceptance is knowable — and it overrules the
        //    local cache the Home gate had to fall back on while signed out. A returning player whose
        //    recorded acceptance predates the current terms is asked again HERE, before the starter claim.
        await EnsureTermsAcceptedAsync();

        // 4. Claim starters so the player lands already owning a roster (idempotent; a returning
        //    player who already claimed just gets their current roster back).
        if (state.Player is { StarterClaimed: false })
            return await ClaimStartersAsync();
        return await api.Heroes.MineAsync();
    }

    /// <summary>
    /// For a SIGNED-IN player: if the server's recorded acceptance doesn't cover the current terms, open the
    /// gate and record the answer server-side before returning. Throws if the player declines — the caller's
    /// flow (which is about to mint or stake) must not continue.
    /// </summary>
    private async Task EnsureTermsAcceptedAsync()
    {
        if (!terms.MustAccept(state.Player)) return;   // the server already holds a current acceptance

        // The no-argument overload: it asks unless THIS session already collected an answer. Deliberately
        // not the cache-consulting one — the server has just told us this player's acceptance is missing or
        // stale, and a cached "somebody using this browser once agreed" must not be allowed to answer on
        // their behalf and produce a durable record of a disclosure nobody saw.
        if (!await terms.RequestAcceptanceAsync())
            throw new GameWalletException("You need to accept the Terms of Use before you can play.");

        // Recording a version already on file is a server-side no-op, so this is safe even when the gate's
        // own Accept (which posts whenever a player IS signed in) has just done it.
        await api.Players.AcceptTermsAsync(Terms.CurrentVersion);
        state.SetPlayer(await api.Players.MeAsync());
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

        // 2. Deposit the fee + both parents into the escrow (plain sends to one opaque address),
        //    each waiting for its spend to settle before the next so they don't contend for one coin.
        //    THE FEE GOES FIRST: it is small and failing it costs nothing, while each parent deposit is
        //    an irreversible send recoverable only through the timelocked reclaim leaf. Paying first means
        //    a player who cannot cover the fee still holds both parents. Order is inert to the server —
        //    IsBreedEscrowFundedAsync is a conjunction over FINAL state (both parents AND the fee present).
        var heroA = await api.Heroes.GetAsync(parentAId);
        var heroB = await api.Heroes.GetAsync(parentBId);
        if (commit.EscrowFeeSats > 0)
        {
            onProgress?.Invoke("Paying the breeding fee…");
            await DepositAndSettleAsync(w.Id, commit.EscrowAddress, null, commit.EscrowFeeSats);
        }
        onProgress?.Invoke("Escrowing the first parent…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroA.AssetId ?? heroA.Id, 0);
        onProgress?.Invoke("Escrowing the second parent…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroB.AssetId ?? heroB.Id, 0);

        // 3. Reveal — retry while the deposits settle into arkd's indexer (the funding gate).
        //    Isolated so a timed-out reveal can be retried alone (the parents are already escrowed).
        onProgress?.Invoke("Minting the child under species control…");
        return await RevealBreedChildAsync(commit.BreedingId, onProgress);
    }

    /// <summary>
    /// Reveal the child for an already-funded breed commit, retrying while the escrow deposits settle
    /// into arkd's indexer. On exhaustion throws <see cref="RevealPendingException"/> carrying the
    /// breeding id — the UI retries THIS (not the whole flow, which would re-deposit spent parents).
    /// </summary>
    public async Task<HeroDto> RevealBreedChildAsync(string breedingId, Action<string>? onProgress = null)
    {
        var nonce = RandomNonce();
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Breeding.RevealAsync(breedingId, new BreedRevealRequest(nonce))).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("breed escrow", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Escrow still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new RevealPendingException(breedingId,
            $"The escrow deposits haven't settled yet — you can retry the reveal in a moment. ({last?.Message})");
    }

    /// <summary>
    /// Pay an accepted stud proposal and take the child — the proposer's half of the stud service. Accepting
    /// is the STUD OWNER's call and has already happened by the time this runs (it is what created these
    /// invoices); this pays the breed fee and the stud fee with plain non-custodial sends, then reveals.
    ///
    /// <para>The breed fee goes first: it is the treasury's own charge, and failing it costs nothing. The
    /// stud fee is the one the server forwards to the other player, so it is sent second — a proposer who
    /// cannot cover both has not paid for a service they won't receive.</para>
    /// </summary>
    public async Task<HeroDto> PayAndRevealStudAsync(string proposalId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        // Read the bill rather than carry it: the accept response was handed to the stud's owner, and this
        // browser may only ever have seen the proposal in a list.
        onProgress?.Invoke("Reading the stud terms…");
        var bill = await api.Stud.InvoicesAsync(proposalId);
        if (bill.BreedFeeInvoice.AmountSats > 0)
        {
            onProgress?.Invoke("Paying the breeding fee…");
            await DepositAndSettleAsync(w.Id, bill.BreedFeeInvoice.PayToAddress, null, bill.BreedFeeInvoice.AmountSats);
        }
        if (bill.StudFeeInvoice is { AmountSats: > 0 } studFee)
        {
            onProgress?.Invoke("Paying the stud fee…");
            await DepositAndSettleAsync(w.Id, studFee.PayToAddress, null, studFee.AmountSats);
        }

        // Reveal — retry while the payments settle into arkd's indexer (the reveal's gate), the same shape
        // the gauntlet and item claim use. ONE nonce across the retries, so a settling payment can't turn
        // into a different child.
        onProgress?.Invoke("Minting the foal…");
        var nonce = RandomNonce();
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Stud.RevealAsync(proposalId, new StudRevealRequest(nonce))).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("not been paid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Payment still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new RevealPendingException(proposalId,
            $"Paid on-chain, but the payments haven't settled for the reveal yet — you can retry in a moment. ({last?.Message})");
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

        // Fee first, then the heroes — failing the fee costs nothing, while each hero deposit is
        // irreversible until the timelocked reclaim leaf opens. IsMergeEscrowFundedAsync is a conjunction
        // over final state (base AND sacrifice AND fee), so the order is inert to the server.
        var heroBase = await api.Heroes.GetAsync(baseId);
        var heroSac = await api.Heroes.GetAsync(sacrificeId);
        if (commit.FeeSats > 0)
        {
            onProgress?.Invoke("Paying the fusion fee…");
            await DepositAndSettleAsync(w.Id, commit.EscrowAddress, null, commit.FeeSats);
        }
        onProgress?.Invoke("Escrowing the base hero…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroBase.AssetId ?? heroBase.Id, 0);
        onProgress?.Invoke("Escrowing the sacrifice…");
        await DepositAndSettleAsync(w.Id, commit.EscrowAddress, heroSac.AssetId ?? heroSac.Id, 0);

        onProgress?.Invoke("Forging the fused hero…");
        return await RevealMergedHeroAsync(commit.MergeId, onProgress);
    }

    /// <summary>
    /// Reveal the fused hero for an already-funded merge commit, retrying while the escrow deposits
    /// settle. On exhaustion throws <see cref="RevealPendingException"/> carrying the merge id — the
    /// UI retries THIS alone (the base + sacrifice are already escrowed).
    /// </summary>
    public async Task<HeroDto> RevealMergedHeroAsync(string mergeId, Action<string>? onProgress = null)
    {
        var nonce = RandomNonce();
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return (await api.Merge.RevealAsync(mergeId, new MergeRevealRequest(nonce))).Hero;
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("merge escrow", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Escrow still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        throw new RevealPendingException(mergeId,
            $"The escrow deposits haven't settled yet — you can retry the reveal in a moment. ({last?.Message})");
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
        //    There is no fee step: the marketplace cut is baked into the offer's covenant and taken from
        //    the sale, so a listing costs the seller nothing up front and cannot be stranded by a failed
        //    fee payment. They receive ask − fee if and when it sells.
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

    /// <summary>Claims a custom, globally-unique name for a hero: requests the rename (the server bills a
    /// treasury fee-invoice), pays it from the wallet, then confirms — returning the renamed hero.</summary>
    public async Task<HeroDto> RenameHeroAsync(string heroId, string name, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Reserving the name…");
        var resp = await api.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest(name));

        if (resp.Fee is { AmountSats: > 0 } fee)
        {
            var w = await wallet.GetActiveWalletAsync()
                ?? throw new GameWalletException("Create a wallet first.");
            onProgress?.Invoke($"Paying the {fee.AmountSats}-sat name fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }

        onProgress?.Invoke("Engraving the name…");
        return await api.Heroes.ConfirmRenameAsync(heroId);
    }

    // ── Tournaments: open/join pay a buy-in into the treasury; resolve runs the bracket ──

    /// <summary>Opens a buy-in tournament (joining as entrant #1) and pays the opener's buy-in from the wallet.</summary>
    public async Task<TournamentDto> OpenTournamentAsync(string heroId, long buyInSats, int size, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Opening the tournament…");
        var resp = await api.Tournament.OpenAsync(new OpenTournamentRequest(heroId, buyInSats, size));
        await PayTournamentBuyInAsync(resp.BuyIn, onProgress);
        return resp.Tournament;
    }

    /// <summary>Joins a hero to an open tournament and pays the entrant's buy-in from the wallet.</summary>
    public async Task<TournamentDto> JoinTournamentAsync(string tournamentId, string heroId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Joining the tournament…");
        var resp = await api.Tournament.JoinAsync(tournamentId, new JoinTournamentRequest(heroId));
        await PayTournamentBuyInAsync(resp.BuyIn, onProgress);
        return resp.Tournament;
    }

    /// <summary>Resolves a full bracket (revealing a fresh nonce); the server pays the podium from the pot.</summary>
    public Task<TournamentResolveResponse> ResolveTournamentAsync(string tournamentId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Running the bracket…");
        return api.Tournament.ResolveAsync(tournamentId, new FightRequest(Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Trustlessly verify a RESOLVED tournament in the browser — recompute the whole bracket from the
    /// revealed seed and check the champion who took the real-sats pot. The fill-time entrant-set commitment
    /// is taken from the tournament's OWN DTO, never the server-supplied replay, so a server that substituted
    /// an entrant's genome/level/gear (with a self-consistent replay) is still caught (mirrors #104). Purely
    /// a read + client-side recompute: no wallet, no fee — anyone can run it on anyone's bracket.
    /// </summary>
    public async Task<(bool Ok, string Detail)> VerifyTournamentAsync(string tournamentId)
    {
        var dto = await api.Tournament.GetAsync(tournamentId);
        var replay = await api.Tournament.ReplayAsync(tournamentId);
        var (cfg, cfgError) = await api.Config.ResolveAsync(replay.ConfigVersion);
        if (cfg is null) return (false, cfgError!);
        return FairnessAudit.VerifyTournament(
            tournamentId, replay.Nonce, replay.CommitmentHex, dto.EntrantsCommitmentHex ?? "", replay, cfg);
    }

    private async Task PayTournamentBuyInAsync(FeeInvoiceDto buyIn, Action<string>? onProgress)
    {
        if (buyIn is not { AmountSats: > 0 }) return;
        var w = await wallet.GetActiveWalletAsync() ?? throw new GameWalletException("Create a wallet first.");
        onProgress?.Invoke($"Paying the {buyIn.AmountSats}-sat buy-in…");
        await DepositAndSettleAsync(w.Id, buyIn.PayToAddress, null, buyIn.AmountSats);
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

    /// <summary>
    /// Run the solo PvE gauntlet from the browser wallet: open (commit + entry fee), pay the fee with a
    /// plain non-custodial send (the same deposit-and-settle primitive breed/merge/buy use), then run the
    /// 5 ghost waves. The server resolves the waves against the paid, committed seed and awards the capped
    /// XP + a full-clear item. Retries the run while the fee settles into arkd's indexer, then CLIENT-VERIFIES
    /// the outcome (re-derives the ghosts + fights from the revealed seed, re-checks the capped XP + item
    /// against the signed receipt) so a server can't pick soft foes or over-award. Returns the verified outcome.
    /// </summary>
    public async Task<GauntletOutcome> RunGauntletAsync(string heroId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Sealing the gauntlet…");
        var open = await api.Gauntlet.OpenAsync(heroId);
        if (open.FeeInvoice.AmountSats > 0)
        {
            onProgress?.Invoke("Paying the entry fee…");
            await DepositAndSettleAsync(w.Id, open.FeeInvoice.PayToAddress, null, open.FeeInvoice.AmountSats);
        }

        // Run — retry while the fee deposit settles into arkd's indexer (the run's gate).
        onProgress?.Invoke("Entering the gauntlet…");
        var nonce = RandomNonce();
        GauntletRunResponse? run = null;
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20 && run is null; attempt++)
        {
            try
            {
                run = await api.Gauntlet.RunAsync(open.GauntletId, nonce);
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("not been paid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Fee still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        if (run is null)
            throw new GameWalletException(
                $"Paid the entry fee on-chain, but it hasn't settled for the run yet — try again in a moment. ({last?.Message})");

        // Client-side fairness recompute — the same gate the console client applies, under the rules the
        // server stamped on the run (an unresolvable stamp fails LOUDLY rather than replaying under Default).
        var (cfg, cfgError) = await api.Config.ResolveAsync(run.ConfigVersion);
        if (cfg is null) return new GauntletOutcome(run, false, cfgError!);
        var (ok, detail) = FairnessAudit.VerifyGauntlet(open.GauntletId, nonce, run.Receipt.CommitmentHex, run, cfg);
        return new GauntletOutcome(run, ok, detail);
    }

    /// <summary>
    /// Run the endless solo Trials: open (commit the seed) → run (reveal a nonce). FREE — no entry fee, so
    /// unlike the gauntlet there is no wallet spend and no settle-race to retry around; it's two plain API
    /// calls. Then CLIENT-VERIFIES the outcome (re-derives the whole ghost ladder from the revealed seed and
    /// re-checks the score + title against the signed receipt) so a server can't pick soft foes or inflate a
    /// leaderboard score. Returns the verified outcome.
    /// </summary>
    public async Task<TrialsOutcome> RunTrialsAsync(string heroId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Sealing the run…");
        var open = await api.Trials.OpenAsync(heroId);

        onProgress?.Invoke("Descending the ladder…");
        var nonce = RandomNonce();
        var run = await api.Trials.RunAsync(open.TrialsId, nonce);

        // Client-side fairness recompute — the same gate every other resolved outcome gets, under the rules
        // the server stamped on the run.
        var (cfg, cfgError) = await api.Config.ResolveAsync(run.ConfigVersion);
        if (cfg is null) return new TrialsOutcome(run, false, cfgError!);
        var (ok, detail) = FairnessAudit.VerifyTrials(open.TrialsId, nonce, run.Receipt.CommitmentHex, run, cfg);
        return new TrialsOutcome(run, ok, detail);
    }

    // ── Daily engagement loop ──
    public Task<DailyStatusDto> DailyStatusAsync() => api.Daily.StatusAsync();

    /// <summary>Season-pass standing — the season-long goal that carries on after the daily is claimed.</summary>
    public Task<SeasonPassProgress> SeasonPassAsync() => api.Players.SeasonPassAsync();

    /// <summary>Claim the daily reward; the sats land in the player's wallet, so refresh the balance pill.</summary>
    public async Task<DailyClaimResultDto> ClaimDailyAsync()
    {
        var result = await api.Daily.ClaimAsync();
        var w = await wallet.GetActiveWalletAsync();
        if (w is not null) state.UpdateBalance(await wallet.GetBalanceAsync(w.Id));
        return result;
    }

    // ── Achievements ──
    /// <summary>The signed-in player's derived milestones + unlocked badges. A pure server read (roster + resolved tournaments); no wallet touch.</summary>
    public Task<PlayerAchievementsDto> AchievementsAsync() => api.Players.AchievementsAsync();

    /// <summary>
    /// Open a wagered match under covenant enforcement and stake into it from the browser wallet:
    /// open (the server returns the challenger's per-party escrow address + the per-character match fee),
    /// then two plain non-custodial sends — the wager stake to the escrow, the fee to the treasury.
    /// The defender accepts + stakes separately; the challenger later resolves. Returns the match id.
    /// </summary>
    public async Task<string> ChallengeAsync(string myHeroId, string opponentHeroId, long wagerSats, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Sealing the challenge…");
        var open = await api.Matches.OpenAsync(new OpenMatchRequest(myHeroId, opponentHeroId, wagerSats, "covenant"));
        if (string.IsNullOrEmpty(open.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // Fee first, stake second. The fee is a fixed treasury send; the stake is player-chosen and can be
        // far larger. Failing the fee after staking would leave the wager sitting in the escrow until the
        // timelocked refund opens, so pay the cheap half before committing the expensive one.
        if (open.MatchFeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying your match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        if (open.EscrowStakeSats > 0)
        {
            onProgress?.Invoke($"Staking your {open.EscrowStakeSats:N0} sats…");
            await DepositAndSettleAsync(w.Id, open.EscrowAddress, null, open.EscrowStakeSats);
        }
        return open.MatchId;
    }

    /// <summary>
    /// Accept a wagered match someone challenged your hero to, staking into it from the browser wallet:
    /// accept (the server returns the defender's escrow address + fee), then stake the wager + pay the fee
    /// with plain sends. Once both sides are staked the challenger can resolve the duel.
    /// </summary>
    public async Task AcceptMatchAsync(string matchId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Accepting the challenge…");
        var accept = await api.Matches.AcceptAsync(matchId);
        if (string.IsNullOrEmpty(accept.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // Fee before stake — same reasoning as the challenger's side.
        if (accept.MatchFeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying your match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        if (accept.EscrowStakeSats > 0)
        {
            onProgress?.Invoke($"Staking your {accept.EscrowStakeSats:N0} sats…");
            await DepositAndSettleAsync(w.Id, accept.EscrowAddress, null, accept.EscrowStakeSats);
        }
    }

    /// <summary>
    /// Resolve an accepted wagered match (challenger only): fight the deterministic duel, retrying while
    /// both stakes + fees settle into arkd's indexer. The SERVER sweeps the covenant to the winner — the
    /// browser only reads the payout back — then CLIENT-VERIFIES the fight replays from the revealed seed.
    /// Returns the fight + the fairness verdict.
    /// </summary>
    public async Task<DuelOutcome> DuelAsync(string matchId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Resolving the duel…");
        var match = await api.Matches.GetAsync(matchId);   // CommitmentHex for the fairness check
        var nonce = RandomNonce();
        FightResponse? fight = null;
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20 && fight is null; attempt++)
        {
            try
            {
                fight = await api.Matches.FightAsync(matchId, new FightRequest(nonce));
            }
            catch (ArkadeHeroesApiException ex) when (
                ex.Message.Contains("not fully funded", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("unpaid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Stakes still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        if (fight is null)
            throw new GameWalletException(
                $"Both sides staked, but the deposits haven't settled for the duel yet — try again in a moment. ({last?.Message})");

        var (cfg, cfgError) = await api.Config.ResolveAsync(fight.ConfigVersion);
        if (cfg is null) return new DuelOutcome(fight, false, cfgError!);
        var (ok, detail) = FairnessAudit.VerifyMatch(matchId, nonce, match.CommitmentHex, fight, cfg);
        return new DuelOutcome(fight, ok, detail);
    }

    // ── Team 3v3 squad matches (wagered): mirror the duel flow with 3-hero lineups ──

    public async Task<string> OpenSquadAsync(IReadOnlyList<string> myLineup, IReadOnlyList<string> opponentLineup,
        long wagerSats, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync() ?? throw new GameWalletException("Create a wallet first.");
        onProgress?.Invoke("Sealing the squad match…");
        var open = await api.Squad.OpenAsync(new OpenSquadMatchRequest(myLineup, opponentLineup, wagerSats, "covenant"));
        if (string.IsNullOrEmpty(open.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");
        // Fee before stake — same reasoning as the duel flow.
        if (open.MatchFeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying your match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        if (open.EscrowStakeSats > 0)
        {
            onProgress?.Invoke($"Staking your {open.EscrowStakeSats:N0} sats…");
            await DepositAndSettleAsync(w.Id, open.EscrowAddress, null, open.EscrowStakeSats);
        }
        return open.MatchId;
    }

    public async Task AcceptSquadAsync(string matchId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync() ?? throw new GameWalletException("Create a wallet first.");
        onProgress?.Invoke("Accepting the squad match…");
        var accept = await api.Squad.AcceptAsync(matchId);
        if (string.IsNullOrEmpty(accept.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");
        // Fee before stake — same reasoning as the duel flow.
        if (accept.MatchFeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying your match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        if (accept.EscrowStakeSats > 0)
        {
            onProgress?.Invoke($"Staking your {accept.EscrowStakeSats:N0} sats…");
            await DepositAndSettleAsync(w.Id, accept.EscrowAddress, null, accept.EscrowStakeSats);
        }
    }

    public async Task<SquadOutcome> ResolveSquadAsync(string matchId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Resolving the best-of-3…");
        var nonce = RandomNonce();
        SquadResolveResponse? res = null;
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20 && res is null; attempt++)
        {
            try { res = await api.Squad.ResolveAsync(matchId, new FightRequest(nonce)); }
            catch (ArkadeHeroesApiException ex) when (
                ex.Message.Contains("not fully funded", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("unpaid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Stakes still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        if (res is null)
            throw new GameWalletException($"Both sides staked, but the deposits haven't settled yet — try again in a moment. ({last?.Message})");

        var replay = await api.Squad.ReplayAsync(matchId);
        var (cfg, cfgError) = await api.Config.ResolveAsync(replay.ConfigVersion);
        var (ok, detail) = cfg is null
            ? (false, cfgError!)
            : FairnessAudit.VerifySquad(matchId, nonce, replay.CommitmentHex, replay, cfg);
        var w = await wallet.GetActiveWalletAsync();
        if (w is not null) state.UpdateBalance(await wallet.GetBalanceAsync(w.Id));
        return new SquadOutcome(res, replay, ok, detail);
    }

    /// <summary>
    /// Open a WINNER-TAKES-ALL death-match and stake into it from the browser wallet — PERMADEATH: if your
    /// hero loses it BURNS and you forfeit your staked gear. Opens (the server returns the ONE joint escrow
    /// + your gear-at-open + the fee), then stakes your hero + each gear unit + the fee with plain asset/sats
    /// sends. The UI MUST gate this behind explicit consent. Returns the death-match id.
    /// </summary>
    public async Task<string> OpenDeathMatchAsync(string myHeroId, string opponentHeroId, bool absorb, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Opening the death-match…");
        var open = await api.DeathMatch.OpenAsync(new DeathMatchOpenRequest(myHeroId, opponentHeroId, absorb));
        if (string.IsNullOrEmpty(open.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // Fee first, stakes second. The fee is a small send to the treasury and failing it costs nothing,
        // whereas the hero and gear go into the joint escrow irreversibly — recoverable only through the
        // reclaim leaf. Paying first means a challenger who cannot cover the fee keeps their hero.
        var myHero = await api.Heroes.GetAsync(myHeroId);
        if (open.FeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying the death-match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        onProgress?.Invoke("Staking your hero into the death-match…");
        await DepositAndSettleAsync(w.Id, open.EscrowAddress, myHero.AssetId ?? myHero.Id, 0);
        foreach (var g in open.ChallengerGear)
        {
            onProgress?.Invoke($"Staking your {g.ItemId}…");
            await DepositAndSettleAsync(w.Id, open.EscrowAddress, g.AssetId, 0, (ulong)g.Amount);
        }
        return open.DeathMatchId;
    }

    /// <summary>
    /// Accept a death-match your hero was challenged to, staking into it — PERMADEATH: your hero burns if it
    /// loses. Accept (returns the joint escrow + your gear-at-open + the fee), then stake your hero + gear +
    /// fee. Once both sides are staked the challenger can resolve it.
    /// </summary>
    public async Task AcceptDeathMatchAsync(string deathMatchId, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");

        onProgress?.Invoke("Accepting the death-match…");
        var accept = await api.DeathMatch.AcceptAsync(deathMatchId);
        if (string.IsNullOrEmpty(accept.EscrowAddress))
            throw new GameWalletException("This arena isn't in covenant mode (no escrow address returned).");

        // Fee first, stakes second — same reasoning as the challenger's side: a defender who cannot cover
        // the fee keeps their hero rather than having it sit in an escrow they must wait out to reclaim.
        if (accept.FeeInvoice is { AmountSats: > 0 } fee)
        {
            onProgress?.Invoke("Paying the death-match fee…");
            await DepositAndSettleAsync(w.Id, fee.PayToAddress, null, fee.AmountSats);
        }
        onProgress?.Invoke("Staking your hero into the death-match…");
        await DepositAndSettleAsync(w.Id, accept.EscrowAddress, accept.DefenderHero.AssetId ?? accept.DefenderHero.Id, 0);
        foreach (var g in accept.DefenderGear)
        {
            onProgress?.Invoke($"Staking your {g.ItemId}…");
            await DepositAndSettleAsync(w.Id, accept.EscrowAddress, g.AssetId, 0, (ulong)g.Amount);
        }
    }

    /// <summary>
    /// Resolve an accepted death-match (challenger only): the SERVER sweeps the joint escrow — routing the
    /// winner's hero + all staked gear to the winner and BURNING the loser's hero — then, in absorb mode, may
    /// re-mint the winner absorbing the loser's traits. Retries while stakes/fees settle, then CLIENT-VERIFIES
    /// the fight (and any absorb) from the revealed seed. Returns the outcome.
    /// </summary>
    public async Task<DeathMatchOutcome> SettleDeathMatchAsync(string deathMatchId, Action<string>? onProgress = null)
    {
        onProgress?.Invoke("Resolving the death-match…");
        var nonce = RandomNonce();
        DeathMatchSettleResponse? settle = null;
        ArkadeHeroesApiException? last = null;
        for (var attempt = 0; attempt < 20 && settle is null; attempt++)
        {
            try
            {
                settle = await api.DeathMatch.SettleAsync(deathMatchId, new DeathMatchSettleRequest(nonce));
            }
            catch (ArkadeHeroesApiException ex) when (
                ex.Message.Contains("must stake", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("hasn't been paid", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                onProgress?.Invoke($"Stakes still settling — retrying ({attempt + 1}/20)…");
                await Task.Delay(3000);
            }
        }
        if (settle is null)
            throw new GameWalletException(
                $"Both heroes staked, but the deposits haven't settled for the resolution yet — try again in a moment. ({last?.Message})");

        // Verify the deterministic fight replays from the revealed seed (VerifyMatch reads only
        // Result/seed/entropy/snapshots; the trailing wager/receipt fields default).
        var fr = new FightResponse(settle.Result, settle.ServerSeedHex, settle.EntropyHex, 0, 0,
            settle.ChallengerSnapshot, settle.DefenderSnapshot, settle.ChallengerSnapshot, settle.DefenderSnapshot);
        var (cfg, cfgError) = await api.Config.ResolveAsync(settle.ConfigVersion);
        var (ok, detail) = cfg is null
            ? (false, cfgError!)
            : FairnessAudit.VerifyMatch(deathMatchId, nonce, settle.Receipt!.CommitmentHex, fr, cfg);

        // Absorb mode: if the winner re-minted, verify the absorbed genome against the seed under the odds
        // the SETTLEMENT ran on — the same stamped config the fight resolved through, not /api/chain/info's
        // current odds (those are whatever is in force now, so a retune would fail honest history).
        if (settle.Minted)
        {
            var challengerWon = settle.WinnerHeroId == settle.ChallengerSnapshot.Id;
            var (aok, adetail) = cfg is null
                ? (false, cfgError!)
                : FairnessAudit.VerifyAbsorb(
                    deathMatchId, settle.ChallengerSnapshot, settle.DefenderSnapshot, challengerWon,
                    nonce, settle.Receipt!.CommitmentHex,
                    settle.Minted, settle.NewGenomeHex, settle.ServerSeedHex, settle.EntropyHex, cfg);
            return new DeathMatchOutcome(settle, ok, detail, aok, adetail);
        }
        return new DeathMatchOutcome(settle, ok, detail);
    }

    /// <summary>Equip an owned item onto one of the player's heroes (server enforces ownership + slot).</summary>
    public async Task<HeroDto> EquipAsync(string heroId, string itemId) =>
        (await api.Heroes.EquipAsync(heroId, new EquipRequest(itemId))).Hero;

    /// <summary>Clear a hero's equipment slot (Weapon/Armor/Trinket), returning the item to the player.</summary>
    public async Task<HeroDto> UnequipAsync(string heroId, string slot) =>
        (await api.Heroes.UnequipAsync(heroId, new UnequipRequest(slot))).Hero;

    /// <summary>
    /// The clock a reclaim timelock actually answers to: the chain's median-time-past, read from the
    /// esplora the server advertises. Deliberately NOT the browser's clock — a covenant reclaim leaf is a
    /// CLTV against consensus time, so a tab with a fast clock would otherwise be told a shut window is
    /// open. Read once per page load and counted down from, since median-time-past only moves as blocks land.
    /// </summary>
    public async Task<long> ChainMedianTimeAsync(CancellationToken ct = default) =>
        await ChainMedianTimeAsync(await api.Chain.InfoAsync(), ct);

    private Task<long> ChainMedianTimeAsync(ChainInfoDto info, CancellationToken ct) =>
        EsploraChainTime.GetMedianTimeAsync(http, string.IsNullOrEmpty(info.EsploraApiUri)
            ? throw new GameWalletException(
                "This arena didn't advertise an esplora API, so the chain clock that governs reclaim timelocks can't be read.")
            : info.EsploraApiUri, ct);

    /// <summary>
    /// Reclaim ONE stranded covenant escrow back to the player's own wallet — the browser half of the
    /// console's <c>canceloffer</c> / <c>refund-breed</c> / <c>refund-merge</c> / <c>refund</c> /
    /// <c>refund-death</c>. Trustless by construction:
    /// the contract is rebuilt in the browser from the escrow's public params and the reclaim leaf is
    /// script-pinned to the player's own address, so the server supplies verifiable parameters and nothing
    /// more — a lying server can make this fail, never divert the asset.
    ///
    /// Runs the SAME flow the console runs (the service-level overload, bound to this browser's NArk DI),
    /// so there is one implementation of each covenant spend rather than a second one that can drift.
    ///
    /// Throws <see cref="RefundNotYetDueException"/> — carrying the due and current chain times — when the
    /// timelock has not opened. The flow raises that BEFORE submitting anything: a refused submission would
    /// permanently poison the canonical txid on arkd, so the window is checked, never probed.
    /// </summary>
    public async Task ReclaimAsync(ReclaimableDto item, Action<string>? onProgress = null)
    {
        var w = await wallet.GetActiveWalletAsync()
            ?? throw new GameWalletException("Create a wallet first.");
        var info = await api.Chain.InfoAsync();
        if (string.IsNullOrEmpty(info.EmulatorUri))
            throw new GameWalletException("This arena isn't in covenant mode (no emulator advertised).");
        var emulator = new Uri(info.EmulatorUri);
        Task<long> ChainTime(CancellationToken ct) => ChainMedianTimeAsync(info, ct);

        // The REGISTERED address, for the same reason BuyHeroAsync uses it: the reclaim leaf is pinned to
        // the address baked into the escrow params, which is the one the server recorded. A fresh receive
        // address advances once funded, so it would not match — the flows compare and refuse rather than
        // build a spend to the wrong script, which is why a mismatch here fails loudly instead of quietly.
        var address = state.Player?.ArkadeAddress ?? await wallet.GetReceiveAddressAsync(w.Id);

        onProgress?.Invoke("Rebuilding the escrow covenant in your browser…");
        switch (item.Kind)
        {
            case "offer":
                await OfferReclaimFlow.ReclaimAsync(services, w.Id, address, emulator,
                    await api.Offers.ParamsAsync(item.Id), ChainTime);
                break;
            case "breed":
                await BreedEscrowRefundFlow.ReclaimAsync(services, w.Id, address, emulator,
                    await api.Breeding.EscrowAsync(item.Id), ChainTime);
                break;
            case "merge":
                await MergeEscrowRefundFlow.ReclaimAsync(services, w.Id, address, emulator,
                    await api.Merge.EscrowAsync(item.Id), ChainTime);
                break;
            case "wager":
                await EscrowRefundFlow.RefundAsync(services, w.Id, address, emulator,
                    await api.Matches.EscrowAsync(item.Id), ChainTime);
                break;
            case "deathmatch":
                // The JOINT escrow: this spends only MY reclaim{Side} leaf, which the covenant bounds to my
                // own hero and gear — so a half-funded escrow (the opponent never staked) comes home too.
                await DeathMatchRefundFlow.ReclaimAsync(services, w.Id, address, emulator,
                    await api.DeathMatch.EscrowAsync(item.Id), ChainTime);
                break;
            default:
                // A kind this build has no flow for. Say so rather than silently doing nothing — the
                // player is looking at escrowed value and needs to know it went unhandled.
                throw new GameWalletException(
                    $"This build can't reclaim a '{item.Kind}' escrow — reclaim it from the console client.");
        }
        onProgress?.Invoke("Reclaim co-signed — the escrow is spending back to your wallet…");
    }

    // One escrow deposit (a hero asset when assetId is set, else sats), then wait for the wallet's
    // coins to re-settle before returning — so the next deposit in the sequence doesn't contend for
    // the just-spent (arkd-locked) BTC coin or race its change syncing back in.
    private async Task DepositAndSettleAsync(string walletId, string escrow, string? assetId, long sats, ulong assetAmount = 1)
    {
        var before = await wallet.SpendableBtcOutpointsAsync(walletId);
        if (assetId is not null)
            await wallet.SendAssetAsync(walletId, escrow, assetId, assetAmount);
        else
            await wallet.SendSatsAsync(walletId, escrow, sats);
        await wallet.WaitForSpendToSettleAsync(walletId, before, TimeSpan.FromSeconds(60));
    }

    private static string RandomNonce() =>
        Convert.ToHexString(RandomUtils.GetBytes(16)).ToLowerInvariant();
}

/// <summary>A completed gauntlet run bundled with its client-side fairness verdict, ready to render.</summary>
public record GauntletOutcome(GauntletRunResponse Run, bool FairnessOk, string FairnessDetail);

/// <summary>A completed endless-Trials run bundled with its client-side fairness verdict, ready to render.</summary>
public record TrialsOutcome(TrialsRunResponse Run, bool FairnessOk, string FairnessDetail);

/// <summary>A resolved wagered duel bundled with its client-side fairness verdict, ready to render.</summary>
public record DuelOutcome(FightResponse Fight, bool FairnessOk, string FairnessDetail);
public record SquadOutcome(SquadResolveResponse Result, SquadReplayDto Replay, bool FairnessOk, string FairnessDetail);

/// <summary>A resolved death-match: the settle result + the fight fairness verdict, plus (absorb mode) the absorbed-genome verdict.</summary>
public record DeathMatchOutcome(
    DeathMatchSettleResponse Settle, bool FairnessOk, string FairnessDetail,
    bool? AbsorbOk = null, string? AbsorbDetail = null);
