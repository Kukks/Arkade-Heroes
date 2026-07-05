using System.Security.Cryptography;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Server;

/// <summary>A rule violation surfaced to the client as HTTP 400.</summary>
public class GameRuleException(string message) : Exception(message);

/// <summary>
/// Orchestrates game flows under the non-custodial mandate: players register
/// their own wallet's Arkade address; every fee/stake is an invoice the
/// player's wallet pays and the server verifies on-chain; the treasury signs
/// only its own outputs (mints, item deliveries, payouts); asset ownership is
/// checked against the chain, never against server records alone.
/// </summary>
public class GameService(GameStore store, IChainService chain, ReceiptSigner receipts, IOptions<GameOptions> options)
{
    private readonly GameOptions _options = options.Value;

    private Shared.ProgressionReceiptDto IssueReceipt(Shared.ProgressionReceiptDto unsigned, params string[] heroIds)
    {
        var receipt = receipts.Issue(unsigned);
        foreach (var heroId in heroIds)
            store.ReceiptsByHero.AddOrUpdate(heroId,
                _ => [receipt],
                (_, list) => { lock (list) { list.Add(receipt); } return list; });
        return receipt;
    }

    private static string NewId(string prefix)
        => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    // ── Players ────────────────────────────────────────────────────────

    /// <summary>How long a login nonce is valid after issuance.</summary>
    private static readonly TimeSpan LoginNonceTtl = TimeSpan.FromMinutes(5);

    public async Task<(Player Player, string Address, long Balance)> RegisterPlayerAsync(
        string name, string arkadeAddress, string? loginPubKeyHex,
        string? nonceHex, string? signatureHex, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new GameRuleException("Player name is required.");
        if (string.IsNullOrWhiteSpace(arkadeAddress))
            throw new GameRuleException("Your wallet's Arkade address is required — keys stay on your side.");

        string? loginKey = null;
        if (!string.IsNullOrWhiteSpace(loginPubKeyHex))
        {
            loginKey = loginPubKeyHex.Trim().ToLowerInvariant();
            // Proof-of-possession: you may only register a login key you actually
            // control — sign a fresh server challenge with it. Without this, an
            // attacker could bind a VICTIM's login pubkey (paired with their own
            // address) to their own player and hijack the victim's later sign-in.
            if (string.IsNullOrWhiteSpace(nonceHex) || string.IsNullOrWhiteSpace(signatureHex))
                throw new GameRuleException("Registering a login key requires proof of possession (a signed challenge).");
            ConsumeAndVerifyChallenge(loginKey, nonceHex, signatureHex);
            // Uniqueness: one player per login key, so sign-in is unambiguous.
            if (store.Players.Values.Any(p =>
                    string.Equals(p.LoginPubKeyHex, loginKey, StringComparison.OrdinalIgnoreCase)))
                throw new GameRuleException("This wallet is already registered — use 'login' to resume it.");
        }

        var player = new Player
        {
            Id = NewId("player"),
            Name = name.Trim(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            LoginPubKeyHex = loginKey,
        };

        try
        {
            await chain.RegisterPlayerAddressAsync(player.Id, arkadeAddress.Trim(), ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new GameRuleException(ex.Message);
        }

        store.Players[player.Id] = player;
        store.PlayersByToken[player.Token] = player;
        var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
        return (player, arkadeAddress.Trim(), balance);
    }

    public Player Authenticate(string? token)
    {
        if (token is not null && store.PlayersByToken.TryGetValue(token, out var player))
            return player;
        throw new GameRuleException("Invalid or missing bearer token.");
    }

    /// <summary>Issues a fresh single-use login nonce (and prunes expired ones).</summary>
    public string IssueLoginChallenge()
    {
        var cutoff = DateTimeOffset.UtcNow - LoginNonceTtl;
        foreach (var (nonce, issued) in store.LoginNonces)
            if (issued < cutoff) store.LoginNonces.TryRemove(nonce, out _);

        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        store.LoginNonces[fresh] = DateTimeOffset.UtcNow;
        return fresh;
    }

    /// <summary>
    /// "Sign in with your wallet": consumes the single-use nonce, verifies the
    /// BIP340 signature over its domain-separated digest, and returns the player
    /// registered with that login key — so a restored wallet resumes its existing
    /// heroes without the server ever holding a key.
    /// </summary>
    public Player Login(string loginPubKeyHex, string nonceHex, string signatureHex)
    {
        var key = string.IsNullOrWhiteSpace(loginPubKeyHex) ? "" : loginPubKeyHex.Trim().ToLowerInvariant();
        ConsumeAndVerifyChallenge(key, nonceHex, signatureHex);

        // SingleOrDefault, not FirstOrDefault: registration enforces one player per
        // login key, so a duplicate would be a broken invariant — fail closed
        // (throw) rather than silently pick one and confuse accounts.
        try
        {
            return store.Players.Values.SingleOrDefault(p =>
                    string.Equals(p.LoginPubKeyHex, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new GameRuleException("No player is registered with this login key.");
        }
        catch (InvalidOperationException)
        {
            throw new GameRuleException("This login key is ambiguous — sign-in refused.");
        }
    }

    /// <summary>
    /// Consumes a single-use challenge nonce (whatever happens next) and verifies
    /// the BIP340 signature over its digest proves control of <paramref name="pubKeyHex"/>.
    /// Shared by login and by registration's proof-of-possession.
    /// </summary>
    private void ConsumeAndVerifyChallenge(string pubKeyHex, string nonceHex, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(nonceHex) || !store.LoginNonces.TryRemove(nonceHex, out var issued))
            throw new GameRuleException("Unknown or already-used challenge — request a fresh one.");
        if (DateTimeOffset.UtcNow - issued > LoginNonceTtl)
            throw new GameRuleException("The challenge expired — request a fresh one.");
        if (!VerifyLoginSignature(pubKeyHex, nonceHex, signatureHex))
            throw new GameRuleException("Signature does not prove control of this login key.");
    }

    private static bool VerifyLoginSignature(string pubKeyHex, string nonceHex, string sigHex)
    {
        try
        {
            var digest = Shared.LoginChallenge.Digest(nonceHex); // 32-byte message
            return NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(pubKeyHex), out var pk) && pk is not null
                && NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(Convert.FromHexString(sigHex), out var sig) && sig is not null
                && pk.SigVerifyBIP340(sig, digest);
        }
        catch { return false; }
    }

    // ── Heroes ─────────────────────────────────────────────────────────

    public Hero GetHero(string heroId)
        => store.Heroes.TryGetValue(heroId, out var hero)
            ? hero
            : throw new GameRuleException($"Unknown hero '{heroId}'.");

    private Hero GetOwnedHero(Player player, string heroId)
    {
        var hero = GetHero(heroId);
        if (hero.OwnerId != player.Id)
            throw new GameRuleException($"Hero '{hero.Name}' does not belong to you.");
        return hero;
    }

    /// <summary>Mints the one-time pair of generation-0 starter heroes to the player's own address.</summary>
    public async Task<IReadOnlyList<Hero>> ClaimStartersAsync(Player player, CancellationToken ct)
    {
        if (player.StarterClaimed) throw new GameRuleException("Starter heroes already claimed.");
        player.StarterClaimed = true; // reserve first so concurrent claims can't double-mint

        // Idempotent under retry: mint only the shortfall to reach two gen-0
        // starters. If a prior attempt minted one hero then failed (e.g. the
        // treasury wasn't funded yet), it stays owned and a re-claim tops up.
        var owned = store.Heroes.Values
            .Where(h => h.OwnerId == player.Id && h.Generation == 0 && h.ParentAId is null)
            .ToList();
        try
        {
            var minted = new List<Hero>();
            for (var i = owned.Count; i < 2; i++)
            {
                var entropy = RandomNumberGenerator.GetBytes(32);
                var genome = Genome.NewGen0(entropy);
                minted.Add(await MintHeroAsync(player, genome, generation: 0,
                    parentA: null, parentB: null,
                    serverSeedHex: Convert.ToHexString(entropy).ToLowerInvariant(),
                    playerNonce: null, entropyHex: null, ct));
            }
            return [.. owned, .. minted];
        }
        catch
        {
            // Release the reservation so the player can retry rather than be
            // stranded hero-less; already-minted heroes remain owned.
            player.StarterClaimed = false;
            throw;
        }
    }

    private async Task<Hero> MintHeroAsync(
        Player player, Genome genome, int generation,
        string? parentA, string? parentB,
        string? serverSeedHex, string? playerNonce, string? entropyHex,
        CancellationToken ct)
    {
        var mint = await chain.MintHeroAssetAsync(player.Id, new HeroMintData(
            genome.ToHex(), generation, parentA, parentB, serverSeedHex, playerNonce), ct);
        return BuildAndStoreHero(player, mint, genome, generation, parentA, parentB, serverSeedHex, playerNonce, entropyHex);
    }

    private Hero BuildAndStoreHero(
        Player player, HeroMintResult mint, Genome genome, int generation,
        string? parentA, string? parentB, string? serverSeedHex, string? playerNonce, string? entropyHex)
    {
        var hero = new Hero
        {
            Id = mint.AssetId,
            OwnerId = player.Id,
            Name = HeroNamer.DeriveName(genome),
            Genome = genome,
            Generation = generation,
            ParentAId = parentA,
            ParentBId = parentB,
            ServerSeedHex = serverSeedHex,
            PlayerNonce = playerNonce,
            EntropyHex = entropyHex,
            AssetId = mint.AssetId,
            MintArkTxId = mint.ArkTxId,
        };
        store.Heroes[hero.Id] = hero;
        return hero;
    }

    // ── Breeding: commit (invoice) → client pays → reveal ──────────────

    public async Task<(BreedingSession Session, FeeInvoice? Invoice)> CommitBreedingAsync(
        Player player, string parentAId, string parentBId, string mode, CancellationToken ct)
    {
        var parentA = GetOwnedHero(player, parentAId);
        var parentB = GetOwnedHero(player, parentBId);

        if (BreedingService.Validate(parentA, parentB, DateTimeOffset.UtcNow) is { } error)
            throw new GameRuleException(error);

        var seed = CommitReveal.NewSeed();
        var sessionId = NewId("breed");

        if (mode == "covenant")
        {
            // The player deposits BOTH parents + the fee into the breed escrow;
            // the covenant (not the treasury) then enforces the mint's shape.
            var escrow = await chain.CreateBreedEscrowAsync(
                sessionId, player.Id, parentA.AssetId!, parentB.AssetId!,
                _options.BreedingFeeSats, receipts.PublicKeyHex,
                DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
            var covenantSession = new BreedingSession
            {
                Id = sessionId, PlayerId = player.Id, ParentAId = parentAId, ParentBId = parentBId,
                ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
                Mode = "covenant", EscrowAddress = escrow.EscrowAddress,
            };
            store.Breedings[covenantSession.Id] = covenantSession;
            return (covenantSession, null);
        }

        var invoice = await chain.CreateFeeInvoiceAsync(
            $"breed:{parentAId}+{parentBId}", _options.BreedingFeeSats, ct);
        var session = new BreedingSession
        {
            Id = sessionId,
            PlayerId = player.Id,
            ParentAId = parentAId,
            ParentBId = parentBId,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
            FeeInvoiceId = invoice.InvoiceId,
        };
        store.Breedings[session.Id] = session;
        return (session, invoice);
    }

    public async Task<(Hero Child, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RevealBreedingAsync(
        Player player, string breedingId, string nonce, CancellationToken ct)
    {
        if (!store.Breedings.TryGetValue(breedingId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown breeding session '{breedingId}'.");
        if (session.Completed) throw new GameRuleException("Breeding already completed.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // The deposit must be present: a paid fee invoice (invoice mode) or the
        // parents + fee sitting in the breed escrow (covenant mode).
        if (session.Mode == "covenant")
        {
            if (!await chain.IsBreedEscrowFundedAsync(session.Id, ct))
                throw new GameRuleException("Deposit both parents and the fee into the breed escrow, then reveal.");
        }
        else if (!await chain.IsInvoicePaidAsync(session.FeeInvoiceId!, ct))
        {
            throw new GameRuleException("The breeding fee invoice has not been paid yet — pay it from your wallet, then reveal.");
        }

        var parentA = GetOwnedHero(player, session.ParentAId);
        var parentB = GetOwnedHero(player, session.ParentBId);
        var now = DateTimeOffset.UtcNow;
        if (BreedingService.Validate(parentA, parentB, now) is { } error)
            throw new GameRuleException(error);

        session.Completed = true;

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.ParentAId, session.ParentBId, nonce);
        var policy = new BreedingPolicy(_options.BreedingCooldownBaseUnit);
        var outcome = BreedingService.Breed(parentA, parentB, entropy, policy);

        parentA.BreedCount++;
        parentA.BreedCooldownUntil = now + outcome.ParentACooldown;
        parentB.BreedCount++;
        parentB.BreedCooldownUntil = now + outcome.ParentBCooldown;

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        Hero child;
        if (session.Mode == "covenant")
        {
            // The oracle (game key) attests the child's metadata Merkle root;
            // the covenant binds the on-chain mint to exactly this attestation.
            var childData = new HeroMintData(
                outcome.ChildGenome.ToHex(), outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce);
            var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
                Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                    childData.GenomeHex, childData.Generation, childData.ParentAId ?? "", childData.ParentBId ?? "",
                    childData.ServerSeedHex ?? "", childData.PlayerNonce ?? ""));
            var oracleSig = receipts.SignDigest(root);
            var mint = await chain.ExecuteBreedCovenantAsync(session.Id, childData, oracleSig, ct);
            child = BuildAndStoreHero(player, mint, outcome.ChildGenome, outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce, entropyHex);
        }
        else
        {
            child = await MintHeroAsync(player, outcome.ChildGenome, outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce, entropyHex, ct);
        }
        session.ChildHeroId = child.Id;

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "breeding", session.Id, session.ParentAId, session.ParentBId, child.Id,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, parentA.Level, parentB.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.ParentAId, session.ParentBId, child.Id);

        return (child, serverSeedHex, entropyHex, receipt);
    }

    // ── Matches: open (invoice) → accept (invoice) → fight ─────────────

    public async Task<(MatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> OpenMatchAsync(
        Player player, string challengerHeroId, string defenderHeroId, long wagerSats,
        string mode, CancellationToken ct)
    {
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot fight itself.");
        if (wagerSats < 0)
            throw new GameRuleException("Wager cannot be negative.");
        if (wagerSats > 0 && defender.OwnerId == player.Id)
            throw new GameRuleException("Wagered matches need an opponent — you own both heroes.");
        if (mode is not ("invoice" or "covenant"))
            throw new GameRuleException("Match mode must be 'invoice' or 'covenant'.");
        if (mode == "covenant" && wagerSats <= 0)
            throw new GameRuleException("Covenant matches are for wagers — set WagerSats.");

        var seed = CommitReveal.NewSeed();
        var commitmentHex = CommitReveal.Commit(seed);
        var matchId = NewId("match");

        FeeInvoice? invoice = null;
        FeeInvoice? feeInvoice = null;
        string? escrowChallenger = null;
        string? escrowDefender = null;
        if (wagerSats > 0)
        {
            if (mode == "covenant")
            {
                // The per-party escrow covenants bake in THIS match's seed
                // commitment, both players' addresses, the game oracle key
                // (the receipt key), and a timelocked refund leaf per party.
                var escrow = await chain.CreateWagerEscrowAsync(
                    matchId, player.Id, defender.OwnerId, wagerSats,
                    Convert.FromHexString(commitmentHex), receipts.PublicKeyHex,
                    DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
                escrowChallenger = escrow.ChallengerEscrowAddress;
                escrowDefender = escrow.DefenderEscrowAddress;
            }
            else
            {
                invoice = await chain.CreateFeeInvoiceAsync($"wager-stake:challenger", wagerSats, ct);
            }

            // The per-character match fee: a level-proportional sats sink the
            // challenger pays to the treasury to stage the match (gated at fight,
            // both modes). Separate from the pot — fielding a high-level hero costs
            // sats every staked fight, whoever wins, so idle-training isn't free.
            feeInvoice = await chain.CreateFeeInvoiceAsync(
                $"match-fee:challenger:{matchId}", Leveling.MatchFee(challenger.Level), ct);
        }

        var session = new MatchSession
        {
            Id = matchId,
            ChallengerPlayerId = player.Id,
            ChallengerHeroId = challenger.Id,
            DefenderHeroId = defender.Id,
            ServerSeed = seed,
            CommitmentHex = commitmentHex,
            WagerSats = wagerSats,
            Mode = mode,
            EscrowChallengerAddress = escrowChallenger,
            EscrowDefenderAddress = escrowDefender,
            ChallengerInvoiceId = invoice?.InvoiceId,
            ChallengerFeeInvoiceId = feeInvoice?.InvoiceId,
            DefenderPlayerId = defender.OwnerId,
        };
        store.Matches[session.Id] = session;
        return (session, invoice, feeInvoice);
    }

    /// <summary>
    /// Defender's owner accepts a wagered match. Invoice mode: they receive
    /// their stake invoice. Covenant mode: acceptance is consent — they stake
    /// by paying the escrow address from their own wallet.
    /// </summary>
    public async Task<(MatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> AcceptMatchAsync(
        Player player, string matchId, CancellationToken ct)
    {
        if (!store.Matches.TryGetValue(matchId, out var session))
            throw new GameRuleException($"Unknown match '{matchId}'.");
        if (session.WagerSats == 0)
            throw new GameRuleException("Friendly matches don't need acceptance — the challenger can fight directly.");
        if (session.Status != "open")
            throw new GameRuleException($"Match is {session.Status}, not open.");

        var defender = GetHero(session.DefenderHeroId);
        if (defender.OwnerId != player.Id)
            throw new GameRuleException("Only the defender hero's owner can accept this match.");

        FeeInvoice? invoice = null;
        if (session.Mode == "invoice")
        {
            invoice = await chain.CreateFeeInvoiceAsync($"wager-stake:defender:{matchId}", session.WagerSats, ct);
            session.DefenderInvoiceId = invoice.InvoiceId;
        }
        // The defender's per-character match fee, proportional to their OWN level
        // (both modes) — the same sats sink the challenger paid at open.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"match-fee:defender:{matchId}", Leveling.MatchFee(defender.Level), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return (session, invoice, feeInvoice);
    }

    public async Task<(MatchSession Session, BattleResult Result, string ServerSeedHex, string EntropyHex,
        long ChallengerXp, long DefenderXp,
        Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot, long WinnerPayout,
        Shared.ProgressionReceiptDto Receipt)>
        FightAsync(Player player, string matchId, string nonce, CancellationToken ct)
    {
        if (!store.Matches.TryGetValue(matchId, out var session) || session.ChallengerPlayerId != player.Id)
            throw new GameRuleException($"Unknown match '{matchId}'.");
        var fightable = session.Status == "accepted" || (session.Status == "open" && session.WagerSats == 0);
        if (!fightable)
            throw new GameRuleException(session.Status == "open"
                ? "This wagered match is waiting for the defender's owner to accept."
                : "Match already resolved.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // Wagered: both stakes must actually sit on-chain — at the invoice
        // addresses (invoice mode) or at the escrow covenant (covenant mode).
        if (session.WagerSats > 0)
        {
            if (session.Mode == "covenant")
            {
                if (!await chain.IsEscrowFundedAsync(session.Id, ct))
                    throw new GameRuleException(
                        $"The escrow is not fully funded — each player must stake {session.WagerSats} sats to their own escrow address.");
            }
            else
            {
                if (!await chain.IsInvoicePaidAsync(session.ChallengerInvoiceId!, ct))
                    throw new GameRuleException("Your stake invoice is unpaid — pay it from your wallet first.");
                if (session.DefenderInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderInvoiceId, ct))
                    throw new GameRuleException("The defender's stake invoice is unpaid.");
            }

            // Both fighters must have paid their per-character match fee (the
            // level-proportional sats sink), whichever mode holds the stakes.
            if (session.ChallengerFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.ChallengerFeeInvoiceId, ct))
                throw new GameRuleException("Your match fee is unpaid — pay the per-character fee invoice from your wallet first.");
            if (session.DefenderFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderFeeInvoiceId, ct))
                throw new GameRuleException("The defender's match fee is unpaid.");
        }

        var challenger = GetHero(session.ChallengerHeroId);
        var defender = GetHero(session.DefenderHeroId);

        // Snapshot pre-fight state (level, equipment) — what the engine actually
        // fights with — so clients can replay and verify.
        var challengerSnapshot = challenger.ToDto();
        var defenderSnapshot = defender.ToDto();

        var entropy = CommitReveal.DeriveEntropy(
            session.ServerSeed, session.Id, challenger.Id, defender.Id, nonce);
        var result = BattleEngine.Fight(challenger, defender, entropy);

        var challengerWon = result.WinnerId == challenger.Id;
        var (winner, loser) = challengerWon ? (challenger, defender) : (defender, challenger);
        // Staked fights only: XP is a CONSERVED transfer from loser to winner,
        // scaled by the level gap (pre-fight levels). Friendly fights are
        // practice — no XP. The loser can DELEVEL, so a champion is held by
        // winning, not bought. No on-chain XP mirror: a losable ladder can't be a
        // non-custodial asset you'd have to claw back — progression stays
        // receipt-based (the receipts are the audit trail; the server is the ledger).
        var transfer = session.WagerSats > 0 ? Leveling.XpTransfer(winner.Level, loser.Level) : 0;
        ApplyXp(winner, transfer);
        ApplyXp(loser, -transfer);
        var challengerDelta = challengerWon ? transfer : -transfer;
        var defenderDelta = -challengerDelta;

        session.Status = "resolved";
        session.Result = result;
        session.Nonce = nonce;
        session.EntropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // Wager settlement: covenant mode sweeps the escrow to the winner via
        // the emulator-enforced covenant (revealing the committed seed);
        // invoice mode pays out from the treasury.
        long winnerPayout = 0;
        if (session.WagerSats > 0)
        {
            winnerPayout = session.WagerSats * 2;
            if (session.Mode == "covenant")
            {
                // The oracle authorization: the game key signs exactly one
                // (match, winner-branch) message the covenant script pins.
                var settleMessage = Chain.Covenants.ArkadeCovenants.SettleMessage(session.Id, challengerWon);
                var oracleSignature = receipts.SignDigest(settleMessage);
                await chain.SettleWagerEscrowAsync(session.Id, challengerWon, session.ServerSeed, oracleSignature, ct);
            }
            else
            {
                var winnerOwnerId = challengerWon ? session.ChallengerPlayerId : session.DefenderPlayerId!;
                await chain.PayoutAsync(winnerOwnerId, winnerPayout, $"wager-pot:{session.Id}", ct);
            }
        }

        var serverSeedHexOut = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "match", session.Id, challenger.Id, defender.Id, result.WinnerId,
                serverSeedHexOut, nonce, session.CommitmentHex,
                challengerDelta,
                defenderDelta,
                challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            challenger.Id, defender.Id);

        return (session, result,
            serverSeedHexOut,
            session.EntropyHex,
            challengerDelta,
            defenderDelta,
            challengerSnapshot, defenderSnapshot, winnerPayout, receipt);
    }

    private static void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award);
        hero.Level = level;
        hero.Xp = xp;
    }

    // ── Hero transfer: the player's wallet moves the asset; we verify ──

    public async Task<Hero> ConfirmTransferAsync(
        Player player, string heroId, string toPlayerId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        if (toPlayerId == player.Id)
            throw new GameRuleException("Hero already belongs to you.");
        if (!store.Players.ContainsKey(toPlayerId))
            throw new GameRuleException($"Unknown player '{toPlayerId}'.");

        // Non-custodial: the owner's wallet performs the asset spend itself.
        // We only verify the chain now shows the recipient holding the asset.
        var moved = await chain.VerifyHeroOwnershipAsync(toPlayerId, hero.AssetId ?? hero.Id, ct);
        if (!moved)
            throw new GameRuleException(
                "The chain does not show the recipient holding this hero yet — send the hero asset from your wallet first, then confirm.");

        // Item assets stay in the sender's wallet, so the loadout can't travel.
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);

        hero.OwnerId = toPlayerId;
        return hero;
    }

    // ── Equipment: invoice → client pays → claim delivers the unit ─────

    public async Task<(ItemPurchase Purchase, FeeInvoice Invoice)> CreateItemInvoiceAsync(
        Player player, string itemId, CancellationToken ct)
    {
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        var invoice = await chain.CreateFeeInvoiceAsync($"item:{itemId}", item.PriceSats, ct);
        var purchase = new ItemPurchase
        {
            InvoiceId = invoice.InvoiceId,
            PlayerId = player.Id,
            ItemId = item.Id,
        };
        store.ItemPurchases[invoice.InvoiceId] = purchase;
        return (purchase, invoice);
    }

    public async Task<(string ItemAssetId, string ArkTxId, ulong UnitsHeld)> ClaimItemAsync(
        Player player, string invoiceId, CancellationToken ct)
    {
        if (!store.ItemPurchases.TryGetValue(invoiceId, out var purchase) || purchase.PlayerId != player.Id)
            throw new GameRuleException($"Unknown purchase '{invoiceId}'.");

        // Idempotent success: a claimed purchase re-reports its delivery.
        if (purchase.Status == "claimed")
        {
            var heldAlready = await chain.GetItemAssetBalanceAsync(player.Id, purchase.ItemId, ct);
            return (purchase.ItemAssetId!, purchase.DeliveryTxId!, heldAlready);
        }

        if (!await chain.IsInvoicePaidAsync(invoiceId, ct))
            throw new GameRuleException("The item invoice has not been paid yet — pay it from your wallet, then claim.");

        // pending → delivering, exactly one claimer at a time; a failed delivery
        // returns to pending so the paid purchase stays claimable.
        lock (purchase.Gate)
        {
            if (purchase.Status == "delivering")
                throw new GameRuleException("Delivery already in progress — retry in a moment.");
            if (purchase.Status == "claimed")
                throw new GameRuleException("Purchase already claimed.");
            purchase.Status = "delivering";
        }

        try
        {
            var item = Core.Equipment.ItemCatalog.Find(purchase.ItemId)!;
            var delivery = await chain.DeliverItemAssetAsync(player.Id, item.Id, item.Name, ct);
            purchase.ItemAssetId = delivery.ItemAssetId;
            purchase.DeliveryTxId = delivery.ArkTxId;
            purchase.Status = "claimed";
            var held = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
            return (delivery.ItemAssetId, delivery.ArkTxId, held);
        }
        catch
        {
            purchase.Status = "pending";
            throw;
        }
    }

    public async Task<Hero> EquipAsync(Player player, string heroId, string itemId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        var unitsHeld = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
        var unitsAllocated = store.Heroes.Values.Count(h =>
            h.OwnerId == player.Id &&
            h.Id != hero.Id &&
            h.Equipment.Slots.Values.Contains(item.Id));
        var alreadyOnTargetSlot = hero.Equipment.Slots.TryGetValue(item.Slot, out var current) && current == item.Id;
        if (!alreadyOnTargetSlot && (ulong)unitsAllocated >= unitsHeld)
            throw new GameRuleException(
                $"You hold {unitsHeld} unit(s) of {item.Name} and {unitsAllocated} are already equipped — buy another with 'buy {item.Id}'.");

        hero.Equipment.Equip(item);
        return hero;
    }

    public Hero Unequip(Player player, string heroId, string slotName)
    {
        var hero = GetOwnedHero(player, heroId);
        if (!Enum.TryParse<Core.Equipment.EquipmentSlot>(slotName, ignoreCase: true, out var slot))
            throw new GameRuleException($"Unknown slot '{slotName}' (Weapon/Armor/Trinket).");
        if (!hero.Equipment.Unequip(slot))
            throw new GameRuleException($"{hero.Name} has nothing equipped in {slot}.");
        return hero;
    }

    // ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ──

    /// <summary>
    /// Lists one spare unit of an item for sale: builds the resting-offer
    /// covenant and returns the address the seller deposits the item into. The
    /// covenant pins the seller as payee and enforces the ask, so fulfilment is
    /// trustless — the server is only the discovery index.
    /// </summary>
    public async Task<(OfferListing Listing, OfferInfo Info)> CreateOfferAsync(
        Player player, string itemId, long askSats, CancellationToken ct)
    {
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");
        if (askSats <= 0) throw new GameRuleException("The ask must be a positive number of sats.");

        // Reconcile this seller's existing listings first, so a just-deposited
        // offer is counted as active (item already gone from their wallet) rather
        // than pending (item still reserved in it).
        foreach (var existing in store.Offers.Values
                     .Where(o => o.SellerId == player.Id && o.ItemId == item.Id && o.Status != "closed").ToList())
            await ReconcileOfferAsync(existing, ct);

        // The seller must hold a FREE unit — not one already equipped, nor one
        // reserved in a PENDING offer (its item is still in their wallet, so it
        // is counted in `held`; a funded/active offer's item already left).
        var held = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
        var equipped = (ulong)store.Heroes.Values.Count(h =>
            h.OwnerId == player.Id && h.Equipment.Slots.Values.Contains(item.Id));
        var reserved = (ulong)store.Offers.Values.Count(o =>
            o.SellerId == player.Id && o.ItemId == item.Id && o.Status == "pending");
        if (held <= equipped + reserved)
            throw new GameRuleException(
                $"You hold {held} unit(s) of {item.Name}; {equipped} equipped and {reserved} awaiting deposit — none free to sell.");

        var offerId = NewId("offer");
        var info = await chain.CreateOfferAsync(offerId, player.Id, item.Id, askSats,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
        var listing = new OfferListing
        {
            Id = offerId, SellerId = player.Id, ItemId = item.Id, AskSats = askSats,
            OfferAddress = info.OfferAddress, ItemAssetId = info.ItemAssetId,
            OfferValueSats = info.OfferValueSats, RefundAfterUnixSeconds = info.RefundAfterUnixSeconds,
        };
        store.Offers[offerId] = listing;
        return (listing, info);
    }

    /// <summary>Active (funded, buyable) offers, each reconciled against on-chain truth first.</summary>
    public async Task<IReadOnlyList<OfferListing>> ListOffersAsync(CancellationToken ct)
    {
        foreach (var offer in store.Offers.Values.Where(o => o.Status != "closed").ToList())
            await ReconcileOfferAsync(offer, ct);
        return store.Offers.Values.Where(o => o.Status == "active")
            .OrderBy(o => o.CreatedAt).ToList();
    }

    /// <summary>One offer's current listing, reconciled against on-chain truth.</summary>
    public async Task<OfferListing> GetOfferAsync(string offerId, CancellationToken ct)
    {
        if (!store.Offers.TryGetValue(offerId, out var offer))
            throw new GameRuleException($"Unknown offer '{offerId}'.");
        await ReconcileOfferAsync(offer, ct);
        return offer;
    }

    /// <summary>
    /// Drives the listing's status from on-chain truth: once the item is
    /// observed at the offer address it is <c>active</c>; when it later leaves
    /// (fulfilled by a buyer or reclaimed by the seller) it becomes <c>closed</c>.
    /// </summary>
    private async Task ReconcileOfferAsync(OfferListing offer, CancellationToken ct)
    {
        if (offer.Status == "closed") return;
        if (await chain.IsOfferFundedAsync(offer.Id, ct))
            offer.Status = "active";
        else if (offer.Status == "active")
            offer.Status = "closed";
    }

    // ── Marketplace: hero sales (unique-asset offers) ──────────────────

    /// <summary>
    /// Lists one of the player's HEROES for sale: the hero is a unique asset, so
    /// this reuses the same offer covenant as items — the seller deposits the
    /// hero asset into the offer address, any buyer pays the ask to take it. The
    /// buyer then claims game-side ownership via <see cref="ClaimPurchasedHeroAsync"/>.
    /// </summary>
    public async Task<(OfferListing Listing, OfferInfo Info)> CreateHeroOfferAsync(
        Player player, string heroId, long askSats, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId); // verifies the seller owns it
        if (askSats <= 0) throw new GameRuleException("The ask must be a positive number of sats.");
        if (string.IsNullOrEmpty(hero.AssetId))
            throw new GameRuleException($"{hero.Name} has no on-chain asset to sell.");
        if (store.Offers.Values.Any(o => o.Kind == "hero" && o.HeroId == heroId && o.Status is "pending" or "active"))
            throw new GameRuleException($"{hero.Name} is already listed for sale.");

        var offerId = NewId("offer");
        var info = await chain.CreateHeroOfferAsync(offerId, player.Id, hero.AssetId!, askSats,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
        var listing = new OfferListing
        {
            Id = offerId, SellerId = player.Id, Kind = "hero", ItemId = "", HeroId = heroId,
            AskSats = askSats, OfferAddress = info.OfferAddress, ItemAssetId = info.ItemAssetId,
            OfferValueSats = info.OfferValueSats, RefundAfterUnixSeconds = info.RefundAfterUnixSeconds,
        };
        store.Offers[offerId] = listing;
        return (listing, info);
    }

    /// <summary>
    /// The buyer claims game-side ownership after fulfilling a hero offer from
    /// their own wallet: non-custodial, so the server only VERIFIES the chain now
    /// shows the buyer holding the hero asset, then reassigns the hero record and
    /// strips its equipment (loadouts stay in the seller's wallet, as on transfer).
    /// </summary>
    public async Task<Hero> ClaimPurchasedHeroAsync(Player buyer, string offerId, CancellationToken ct)
    {
        if (!store.Offers.TryGetValue(offerId, out var offer) || offer.Kind != "hero")
            throw new GameRuleException($"Unknown hero offer '{offerId}'.");
        if (offer.SellerId == buyer.Id)
            throw new GameRuleException("You can't buy your own hero.");
        var hero = GetHero(offer.HeroId!);

        var held = await chain.VerifyHeroOwnershipAsync(buyer.Id, hero.AssetId ?? hero.Id, ct);
        if (!held)
            throw new GameRuleException(
                "The chain does not show you holding this hero yet — fulfil the offer from your wallet first, then claim.");

        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);
        hero.OwnerId = buyer.Id;
        offer.Status = "closed";
        return hero;
    }
}
