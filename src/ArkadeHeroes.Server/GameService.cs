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

    public async Task<(Player Player, string Address, long Balance)> RegisterPlayerAsync(
        string name, string arkadeAddress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new GameRuleException("Player name is required.");
        if (string.IsNullOrWhiteSpace(arkadeAddress))
            throw new GameRuleException("Your wallet's Arkade address is required — keys stay on your side.");

        var player = new Player
        {
            Id = NewId("player"),
            Name = name.Trim(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
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
        player.StarterClaimed = true;

        var heroes = new List<Hero>();
        for (var i = 0; i < 2; i++)
        {
            var entropy = RandomNumberGenerator.GetBytes(32);
            var genome = Genome.NewGen0(entropy);
            heroes.Add(await MintHeroAsync(player, genome, generation: 0,
                parentA: null, parentB: null,
                serverSeedHex: Convert.ToHexString(entropy).ToLowerInvariant(),
                playerNonce: null, entropyHex: null, ct));
        }
        return heroes;
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

    public async Task<(MatchSession Session, FeeInvoice? Invoice)> OpenMatchAsync(
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
            DefenderPlayerId = defender.OwnerId,
        };
        store.Matches[session.Id] = session;
        return (session, invoice);
    }

    /// <summary>
    /// Defender's owner accepts a wagered match. Invoice mode: they receive
    /// their stake invoice. Covenant mode: acceptance is consent — they stake
    /// by paying the escrow address from their own wallet.
    /// </summary>
    public async Task<(MatchSession Session, FeeInvoice? Invoice)> AcceptMatchAsync(
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
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return (session, invoice);
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
        var winnerAward = Leveling.WinnerAward(loser.Level);
        var loserAward = Leveling.LoserAward(winner.Level);
        ApplyXp(winner, winnerAward);
        ApplyXp(loser, loserAward);
        // Mirror the earned XP on-chain as a spendable asset balance (the
        // receipts remain the verification root). Delivered in the BACKGROUND:
        // an on-chain XP delivery is a treasury spend that must not add its
        // latency to the duel response, and a hiccup must not fail the resolved
        // match. Uses CancellationToken.None — it outlives the request.
        DeliverXpInBackground(winner.OwnerId, (ulong)winnerAward);
        DeliverXpInBackground(loser.OwnerId, (ulong)loserAward);

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
                challengerWon ? winnerAward : loserAward,
                challengerWon ? loserAward : winnerAward,
                challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            challenger.Id, defender.Id);

        return (session, result,
            serverSeedHexOut,
            session.EntropyHex,
            challengerWon ? winnerAward : loserAward,
            challengerWon ? loserAward : winnerAward,
            challengerSnapshot, defenderSnapshot, winnerPayout, receipt);
    }

    private static void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award);
        hero.Level = level;
        hero.Xp = xp;
    }

    private void DeliverXpInBackground(string playerId, ulong amount)
    {
        if (amount == 0 || !_options.DeliverXpAssetsOnChain) return;
        // Fire-and-forget: chain is a singleton, so this safely outlives the
        // request. The match is already resolved + receipted; XP is best-effort.
        _ = Task.Run(async () =>
        {
            try { await chain.DeliverXpAsync(playerId, amount, CancellationToken.None); }
            catch { /* best-effort on-chain mirror */ }
        });
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
}
