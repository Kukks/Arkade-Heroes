using System.Security.Cryptography;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Core;
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

    // The game-balance config the server runs under (economy from GameOptions; the rest from
    // GameConfig.Default, which the client shares at compile time — so verification matches).
    private readonly GameConfig _config = options.Value.ToGameConfig();

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
        // The breed fee escalates with how much the parents have already been bred
        // (their combined breed count) — a supply-side sats sink.
        var breedFee = BreedingPolicy.FeeSats(_config.BreedingFeeSats, parentA.BreedCount + parentB.BreedCount, _config);

        // Rarity-derived sterility: the rarest heroes can be born unable to breed,
        // capping the supply of legendary lines. Deterministic from the genome.
        if (Sterility.IsSterile(parentA.Genome, _config))
            throw new GameRuleException($"{parentA.Name} is sterile — it cannot breed.");
        if (Sterility.IsSterile(parentB.Genome, _config))
            throw new GameRuleException($"{parentB.Name} is sterile — it cannot breed.");

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
                breedFee, receipts.PublicKeyHex,
                DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
            var covenantSession = new BreedingSession
            {
                Id = sessionId, PlayerId = player.Id, ParentAId = parentAId, ParentBId = parentBId,
                ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
                Mode = "covenant", EscrowAddress = escrow.EscrowAddress, FeeSats = breedFee,
            };
            store.Breedings[covenantSession.Id] = covenantSession;
            return (covenantSession, null);
        }

        var invoice = await chain.CreateFeeInvoiceAsync(
            $"breed:{parentAId}+{parentBId}", breedFee, ct);
        var session = new BreedingSession
        {
            Id = sessionId,
            PlayerId = player.Id,
            ParentAId = parentAId,
            ParentBId = parentBId,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
            FeeInvoiceId = invoice.InvoiceId,
            FeeSats = breedFee,
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
        var outcome = BreedingService.Breed(parentA, parentB, entropy, policy, _config);

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

    // ── PvE gauntlet (F1): open (commit + fee invoice) → client pays → run ──

    public async Task<(GauntletSession Session, FeeInvoice Invoice)> OpenGauntletAsync(
        Player player, string heroId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        var now = DateTimeOffset.UtcNow;
        if (hero.GauntletCooldownUntil is { } until && until > now)
            throw new GameRuleException($"{hero.Name} is resting after its last gauntlet — try again shortly.");

        var seed = CommitReveal.NewSeed();
        var id = NewId("gauntlet");
        var fee = Gauntlet.Fee(hero.Level, _config);
        var invoice = await chain.CreateFeeInvoiceAsync($"gauntlet:{heroId}", fee, ct);
        var session = new GauntletSession
        {
            Id = id, PlayerId = player.Id, HeroId = heroId,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
            FeeInvoiceId = invoice.InvoiceId, FeeSats = fee,
        };
        store.Gauntlets[id] = session;
        return (session, invoice);
    }

    public async Task<(GauntletRun Run, long XpAwarded, Shared.HeroDto HeroSnapshot, string? ItemAwarded, string? ItemAssetId, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RunGauntletAsync(
        Player player, string gauntletId, string nonce, CancellationToken ct)
    {
        if (!store.Gauntlets.TryGetValue(gauntletId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown gauntlet '{gauntletId}'.");
        if (session.Completed) throw new GameRuleException("This gauntlet has already been run.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");
        if (!await chain.IsInvoicePaidAsync(session.FeeInvoiceId, ct))
            throw new GameRuleException("The gauntlet fee invoice has not been paid yet — pay it from your wallet, then run.");

        var hero = GetOwnedHero(player, session.HeroId);
        var heroSnapshot = hero.ToDto();          // pre-run, so the client can replay the ghosts + fights
        var preRunLevel = hero.Level;
        session.Completed = true;

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.HeroId, nonce);
        var run = Gauntlet.Resolve(hero, entropy, _config);

        // Capped, priced XP faucet (anti-farming): the award is computed from the PRE-run level, so a run
        // that crosses the cap keeps its award, but future runs (already past the cap) award nothing.
        var xpAward = Gauntlet.XpForRun(preRunLevel, run.WavesCleared);
        ApplyXp(hero, xpAward);

        // A full clear delivers one entropy-picked 500-sat-tier item to the player's wallet.
        var itemAwarded = Gauntlet.RewardItem(entropy, run.WavesCleared);
        string? itemAssetId = null;
        if (itemAwarded is not null)
        {
            var item = Core.Equipment.ItemCatalog.Find(itemAwarded)!;
            var delivery = await chain.DeliverItemAssetAsync(player.Id, item.Id, item.Name, ct);
            itemAssetId = delivery.ItemAssetId;
        }

        hero.GauntletCooldownUntil = DateTimeOffset.UtcNow + _options.GauntletCooldown;

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
        // Gauntlet receipt (NOT a "match" receipt → carries no leaderboard weight). HeroBId is empty;
        // ResultHeroId = the hero on a full clear; XpAwardA = the award; LevelA = post-run level;
        // LevelB = PRE-run level (so a verifier can recompute the level-10 cap independently).
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "gauntlet", session.Id, session.HeroId, "", run.WavesCleared >= Gauntlet.WaveCount ? session.HeroId : null,
                serverSeedHex, nonce, session.CommitmentHex,
                xpAward, 0, hero.Level, preRunLevel,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.HeroId);

        return (run, xpAward, heroSnapshot, itemAwarded, itemAssetId, serverSeedHex, entropyHex, receipt);
    }

    // ── Merge / fusion: commit (escrow deposit) → reveal ───────────────

    public async Task<(MergeSession Session, string EscrowAddress)> CommitMergeAsync(
        Player player, string baseId, string sacrificeId, string mode, CancellationToken ct)
    {
        if (baseId == sacrificeId)
            throw new GameRuleException("The base and the sacrifice must be two different heroes.");
        var baseHero = GetOwnedHero(player, baseId);
        var sacrificeHero = GetOwnedHero(player, sacrificeId);
        // Sterility does NOT gate being an input — a sterile Legendary is a great
        // sacrifice (feed its rare trait in), which gives sterile rares a use.

        var seed = CommitReveal.NewSeed();
        var sessionId = NewId("merge");
        // Both inputs plus the fee go into the merge escrow; execution retires the two
        // inputs to the treasury (the sink) and mints the fused hero to the player.
        var escrow = await chain.CreateMergeEscrowAsync(
            sessionId, player.Id, baseHero.AssetId!, sacrificeHero.AssetId!,
            _options.MergeFeeSats, receipts.PublicKeyHex,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);

        var session = new MergeSession
        {
            Id = sessionId, PlayerId = player.Id, BaseId = baseId, SacrificeId = sacrificeId,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
            Mode = mode, EscrowAddress = escrow, FeeSats = _options.MergeFeeSats,
        };
        store.Merges[session.Id] = session;
        return (session, escrow);
    }

    public async Task<(Hero Fused, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RevealMergeAsync(
        Player player, string mergeId, string nonce, CancellationToken ct)
    {
        if (!store.Merges.TryGetValue(mergeId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown merge session '{mergeId}'.");
        if (session.Completed) throw new GameRuleException("Merge already completed.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // The deposit must be present: base + sacrifice + fee sitting in the merge escrow.
        if (!await chain.IsMergeEscrowFundedAsync(session.Id, ct))
            throw new GameRuleException("Deposit the base, the sacrifice, and the fee into the merge escrow, then reveal.");

        var baseHero = GetOwnedHero(player, session.BaseId);
        var sacrificeHero = GetOwnedHero(player, session.SacrificeId);

        session.Completed = true;

        // Entropy-seeded fusion: concentration almost always succeeds, but the fused
        // genome (hence its sterility) can't be precomputed — the gamble that keeps
        // sterility meaningful. Deterministic given (seed, ids, nonce).
        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.BaseId, session.SacrificeId, nonce);
        var fusedGenome = Fusion.Fuse(baseHero.Genome, sacrificeHero.Genome, entropy, _config);
        var fusedGeneration = Math.Max(baseHero.Generation, sacrificeHero.Generation) + 1;

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // The oracle (game key) attests the fused hero's metadata Merkle root; rung 2's
        // covenant binds the on-chain mint (inputs retired, fused issued) to this attestation.
        var fusedData = new HeroMintData(
            fusedGenome.ToHex(), fusedGeneration, session.BaseId, session.SacrificeId, serverSeedHex, nonce);
        var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
            Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                fusedData.GenomeHex, fusedData.Generation, fusedData.ParentAId ?? "", fusedData.ParentBId ?? "",
                fusedData.ServerSeedHex ?? "", fusedData.PlayerNonce ?? ""));
        var oracleSig = receipts.SignDigest(root);
        var mint = await chain.ExecuteMergeAsync(session.Id, fusedData, oracleSig, ct);

        var fused = BuildAndStoreHero(player, mint, fusedGenome, fusedGeneration,
            session.BaseId, session.SacrificeId, serverSeedHex, nonce, entropyHex);
        // The fused hero inherits the base's level (you keep your progression); its genesis
        // level is attested by the merge receipt below so ReplayLevel stays consistent.
        fused.Level = baseHero.Level;
        session.FusedHeroId = fused.Id;

        // Both inputs are consumed: drop their server-side records (their assets are
        // on-chain-retired to the treasury by ExecuteMergeAsync).
        store.Heroes.TryRemove(session.BaseId, out _);
        store.Heroes.TryRemove(session.SacrificeId, out _);

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "merge", session.Id, session.BaseId, session.SacrificeId, fused.Id,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, baseHero.Level, sacrificeHero.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.BaseId, session.SacrificeId, fused.Id);

        return (fused, serverSeedHex, entropyHex, receipt);
    }

    // ── Death-match: open → both stake a hero → settle (loser's hero burns) ──

    public async Task<(DeathMatchSession Session, string EscrowAddress, Shared.FavorabilityDto Favorability, IReadOnlyList<Shared.GearStakeDto> ChallengerGear, IReadOnlyList<Shared.GearStakeDto> DefenderGear, FeeInvoice ChallengerFeeInvoice)> OpenDeathMatchAsync(
        Player player, string challengerHeroId, string defenderHeroId, bool absorb, CancellationToken ct)
    {
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot death-match itself.");
        if (defender.OwnerId == player.Id)
            throw new GameRuleException("A death-match needs an opponent — you own both heroes.");

        var seed = CommitReveal.NewSeed();
        var id = NewId("dm");
        var commitment = CommitReveal.Commit(seed);
        var refundAfter = DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds();
        // Covenant-v2: ONE joint escrow baked at open — both parties are known (the
        // defender is the challenged hero's owner). Both players stake into this one
        // address; consent = staking. The settle branch STRUCTURALLY enforces the
        // outcome: burn the loser, return the winner's hero AND ALL staked gear to
        // the winner. Each side's stake = their hero + the item units matching the
        // hero's equipped loadout AT OPEN (unequip before opening to shield gear).
        var challengerGearIds = challenger.Equipment.Slots.Values.ToList();
        var defenderGearIds = defender.Equipment.Slots.Values.ToList();
        // Absorb mode → the 6-leaf escrow bakes the species the absorbed hero mints under.
        var speciesId = absorb ? (await chain.GetInfoAsync(ct)).SpeciesAssetId ?? "" : "";
        var escrow = await chain.CreateDeathMatchJointEscrowAsync(
            id, player.Id, challenger.AssetId!, defender.OwnerId, defender.AssetId!,
            Convert.FromHexString(commitment), receipts.PublicKeyHex, refundAfter,
            challengerGearIds, defenderGearIds, absorb: absorb, speciesId: speciesId, ct: ct);

        // Per-character death-match fee (level-scaled treasury sink) — both sides' fees gate settle.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"dm-fee:challenger:{id}", Leveling.DeathMatchFee(challenger.Level, absorb, _config), ct);

        var session = new DeathMatchSession
        {
            Id = id,
            ChallengerFeeInvoiceId = feeInvoice.InvoiceId,
            ChallengerPlayerId = player.Id,
            DefenderPlayerId = defender.OwnerId,
            ChallengerHeroId = challengerHeroId,
            DefenderHeroId = defenderHeroId,
            ServerSeed = seed,
            CommitmentHex = commitment,
            JointEscrowAddress = escrow,
            ChallengerGearItemIds = challengerGearIds,
            DefenderGearItemIds = defenderGearIds,
            Absorb = absorb,
            SpeciesId = speciesId,
        };
        store.DeathMatches[session.Id] = session;
        // Favorability from realized POWER (F18) — gear is staked here, so a level read would lie.
        var favor = new Shared.FavorabilityDto(defender.Level - challenger.Level,
            Matchmaking.PowerFavor(PowerScore.Compute(challenger, _config), PowerScore.Compute(defender, _config)));
        var escrowParams = await chain.GetDeathMatchEscrowParamsAsync(id, ct);
        return (session, escrow, favor, MapGearDtos(escrowParams?.ChallengerGear), MapGearDtos(escrowParams?.DefenderGear), feeInvoice);
    }

    /// <summary>The chain-resolved gear stakes as client-facing deposit instructions (ItemId is display provenance; AssetId is what gets sent).</summary>
    private static IReadOnlyList<Shared.GearStakeDto> MapGearDtos(IReadOnlyList<Chain.Covenants.GearStake>? stakes)
        => stakes?.Select(s => new Shared.GearStakeDto(s.ItemId ?? s.AssetId, s.AssetId, s.Amount)).ToList() ?? [];

    public async Task<(DeathMatchSession Session, string EscrowAddress, Hero Defender, IReadOnlyList<Shared.GearStakeDto> DefenderGear, FeeInvoice DefenderFeeInvoice)> AcceptDeathMatchAsync(
        Player player, string deathMatchId, CancellationToken ct)
    {
        if (!store.DeathMatches.TryGetValue(deathMatchId, out var session))
            throw new GameRuleException($"Unknown death-match '{deathMatchId}'.");
        if (session.DefenderPlayerId != player.Id)
            throw new GameRuleException("Only the challenged hero's owner can accept this death-match.");
        if (session.Accepted) throw new GameRuleException("Death-match already accepted.");
        if (session.Completed) throw new GameRuleException("Death-match already resolved.");
        var defender = GetOwnedHero(player, session.DefenderHeroId);

        // Covenant-v2: no new escrow — the joint escrow was baked at open. Accepting =
        // staking the defender's hero (+ their baked gear) into the SAME joint address
        // (consent = staking).
        session.Accepted = true;
        // Defender's death-match fee — mirrors the wager defender fee; gated at settle.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"dm-fee:defender:{deathMatchId}", Leveling.DeathMatchFee(defender.Level, session.Absorb, _config), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        var escrowParams = await chain.GetDeathMatchEscrowParamsAsync(deathMatchId, ct);
        return (session, session.JointEscrowAddress!, defender, MapGearDtos(escrowParams?.DefenderGear), feeInvoice);
    }

    public async Task<(Shared.BattleResultDto Result, string WinnerHeroId, string LoserHeroId, Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt, bool Minted, int TraitsAbsorbed, string? NewGenomeHex, Shared.HeroDto? NewHero)> SettleDeathMatchAsync(
        Player player, string deathMatchId, string nonce, CancellationToken ct)
    {
        if (!store.DeathMatches.TryGetValue(deathMatchId, out var session))
            throw new GameRuleException($"Unknown death-match '{deathMatchId}'.");
        if (session.ChallengerPlayerId != player.Id && session.DefenderPlayerId != player.Id)
            throw new GameRuleException("Only a participant can settle this death-match.");
        if (session.Completed) throw new GameRuleException("Death-match already resolved.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // Both players must have staked their hero into the one joint escrow.
        if (!await chain.IsDeathMatchEscrowFundedAsync(deathMatchId, ct))
            throw new GameRuleException("Both players must stake their hero before the death-match settles.");

        // Both per-character death-match fees must be paid (mirrors the wager fight gate; never blocks the refund path).
        if (session.ChallengerFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.ChallengerFeeInvoiceId, ct))
            throw new GameRuleException("The challenger's death-match fee hasn't been paid yet.");
        if (session.DefenderFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderFeeInvoiceId, ct))
            throw new GameRuleException("The defender's death-match fee hasn't been paid yet.");

        var challenger = GetHero(session.ChallengerHeroId);
        var defender = GetHero(session.DefenderHeroId);
        // Pre-fight snapshots — what the engine fights with — so the client can replay + verify the winner.
        var challengerSnapshot = challenger.ToDto();
        var defenderSnapshot = defender.ToDto();

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, challenger.Id, defender.Id, nonce);
        var result = BattleEngine.Fight(challenger, defender, entropy, _config);
        var challengerWon = result.WinnerId == challenger.Id;
        var (winner, loser) = challengerWon ? (challenger, defender) : (defender, challenger);
        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // Persist the fight-time replay data before the branches (both burn a hero) so ANY spectator can
        // watch + verify this death-match later — the same trustless replay as a wager match.
        session.Result = result;
        session.ChallengerSnapshot = challengerSnapshot;
        session.DefenderSnapshot = defenderSnapshot;
        session.EntropyHex = entropyHex;
        session.Nonce = nonce;

        // ── ABSORB MODE: a seed-driven roll may RE-MINT the winner absorbing the loser's better
        // traits — BOTH heroes burn and a new hero mints under species to the winner. A failed roll
        // (or a classic match) falls through to the keep path (the winner keeps its exact hero).
        if (session.Absorb)
        {
            var outcome = Absorb.Resolve(winner.Genome, loser.Genome, entropy,
                _config.Absorb);
            if (outcome.Minted)
            {
                var absorbGen = Math.Max(winner.Generation, loser.Generation) + 1;
                var absorbedData = new HeroMintData(outcome.Result.ToHex(), absorbGen, winner.Id, loser.Id, serverSeedHex, nonce);
                // The oracle (game key) attests BOTH the winner (absorb-mint message) AND the absorbed
                // genome root; the covenant binds the burn+mint to exactly these. Chain FIRST (retryable).
                var outcomeSig = receipts.SignDigest(Chain.Covenants.ArkadeCovenants.DeathMatchAbsorbMintMessage(session.Id, challengerWon));
                var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
                    Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                        absorbedData.GenomeHex, absorbedData.Generation, absorbedData.ParentAId ?? "", absorbedData.ParentBId ?? "",
                        absorbedData.ServerSeedHex ?? "", absorbedData.PlayerNonce ?? ""));
                var rootSig = receipts.SignDigest(root);
                var mint = await chain.SettleDeathMatchAbsorbMintAsync(session.Id, challengerWon, absorbedData, session.ServerSeed, outcomeSig, rootSig, ct);

                session.Completed = true;
                session.WinnerHeroId = result.WinnerId;
                // The absorbed hero is a NEW asset owned by the WINNER (the settler may be the loser).
                var winnerPlayer = store.Players[winner.OwnerId];
                var absorbed = BuildAndStoreHero(winnerPlayer, mint, outcome.Result, absorbGen,
                    winner.Id, loser.Id, serverSeedHex, nonce, entropyHex);
                absorbed.Level = winner.Level;   // the winner keeps its progression (absorb receipt attests it)
                absorbed.Name = winner.Name;     // the same hero, evolved — keep its name
                // BOTH input heroes are burned on-chain — drop their server records.
                store.Heroes.TryRemove(winner.Id, out _);
                store.Heroes.TryRemove(loser.Id, out _);

                var absorbReceipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                        "absorb", session.Id, session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id,
                        serverSeedHex, nonce, session.CommitmentHex,
                        0, 0, winner.Level, loser.Level,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
                    session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id);
                return (result.ToDto(), result.WinnerId, loser.Id, challengerSnapshot, defenderSnapshot,
                    serverSeedHex, entropyHex, absorbReceipt, true, outcome.TraitsAbsorbed, outcome.Result.ToHex(), absorbed.ToDto());
            }
        }

        // ── KEEP PATH (classic death-match, or an absorb roll that didn't fire): the loser's hero
        // is BURNED and the winner keeps its exact hero. The oracle signs the keep branch. Chain
        // FIRST (deterministic fight re-runs identically, so a retry surfaces the real error).
        var settleMessage = Chain.Covenants.ArkadeCovenants.DeathMatchSettleMessage(session.Id, challengerWon);
        var oracleSig = receipts.SignDigest(settleMessage);
        await chain.SettleDeathMatchAsync(session.Id, challengerWon, session.ServerSeed, oracleSig, ct);

        session.Completed = true;
        session.WinnerHeroId = result.WinnerId;
        store.Heroes.TryRemove(loser.Id, out _);

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "deathmatch", session.Id, session.ChallengerHeroId, session.DefenderHeroId, result.WinnerId,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.ChallengerHeroId, session.DefenderHeroId);

        return (result.ToDto(), result.WinnerId, loser.Id, challengerSnapshot, defenderSnapshot,
            serverSeedHex, entropyHex, receipt, false, 0, null, null);
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
        long? refundAfterUnix = null;
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
                refundAfterUnix = escrow.RefundAfterUnixSeconds;
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
                $"match-fee:challenger:{matchId}", Leveling.MatchFee(challenger.Level, _config), ct);
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
            RefundAfterUnixSeconds = refundAfterUnix,
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
            $"match-fee:defender:{matchId}", Leveling.MatchFee(defender.Level, _config), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return (session, invoice, feeInvoice);
    }

    /// <summary>
    /// Marks stale covenant matches 'expired' so the match list drops them: past
    /// its refund window, an OPEN match whose challenger stake is gone (never
    /// staked, or refunded) or an ACCEPTED match missing either stake is
    /// abandoned. A still-fully-funded match stays visible — it can yet settle or
    /// be refunded. Within the window nothing is touched, so a live pending match
    /// is never mis-marked (this is why it needs PER-PARTY funding — a single
    /// both-parties probe can't tell "defender hasn't staked yet" from "challenger
    /// refunded"). Runs lazily on match listing; a no-op in invoice mode.
    /// </summary>
    public async Task ReconcileAbandonedMatchesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var m in store.Matches.Values)
        {
            if (m.Mode != "covenant" || m.Status is not ("open" or "accepted")) continue;
            if (m.RefundAfterUnixSeconds is not { } refundAfter || now < refundAfter) continue;
            var funding = await chain.GetWagerEscrowFundingAsync(m.Id, ct);
            if (funding is null) continue;
            var abandoned = m.Status == "open"
                ? !funding.ChallengerFunded
                : !(funding.ChallengerFunded && funding.DefenderFunded);
            if (abandoned) m.Status = "expired";
        }
    }

    /// <summary>
    /// XP-weighted matchmaking: OTHER players' heroes ranked by how evenly matched
    /// they are with the given hero (closest level first), each annotated with the
    /// conserved XP swing — what a staked win would gain and a loss would cost — so
    /// a player finds fights where XP is actually at stake, not lopsided ones.
    /// </summary>
    public IReadOnlyList<Shared.OpponentSuggestionDto> SuggestOpponents(Player player, string heroId, int? take = null)
    {
        var hero = GetOwnedHero(player, heroId);
        var heroPower = PowerScore.Compute(hero, _config);
        return store.Heroes.Values
            .Where(h => h.OwnerId != player.Id)
            .Select(h =>
            {
                var oppPower = PowerScore.Compute(h, _config);
                return new Shared.OpponentSuggestionDto(
                    h.ToDto(), h.OwnerId,
                    Matchmaking.LevelGap(hero.Level, h.Level),
                    Matchmaking.XpIfWin(hero.Level, h.Level),
                    Matchmaking.XpIfLose(hero.Level, h.Level),
                    oppPower,
                    Matchmaking.PowerGapPercent(heroPower, oppPower),
                    // F2: the free-underdog-shot label rides along the (level-based) conserved swings.
                    Matchmaking.Favor(hero.Level, h.Level));
            })
            // Closest realized-power fights first (F18); level gap + a stable id keep a total order.
            .OrderBy(s => s.PowerGapPercent)
            .ThenBy(s => s.LevelGap)
            .ThenBy(s => s.Hero.Id, StringComparer.Ordinal)
            .Take(take ?? _config.MatchmakingTake)
            .ToList();
    }

    /// <summary>The current season's ranked ladder: staked-match wins tallied over the receipts that fall
    /// within the season window (reusing <see cref="Shared.LeaderboardBuilder"/>), plus when the season ends.
    /// Trustless + auto-resetting — computed from the signed receipts, and the window rolls with the clock.</summary>
    public Shared.SeasonLeaderboardDto SeasonLeaderboard()
    {
        var season = Season.Current(DateTimeOffset.UtcNow, _config.SeasonLengthDays);
        var startUnix = season.Start.ToUnixTimeSeconds();
        var endUnix = season.End.ToUnixTimeSeconds();
        var heroes = store.Heroes.Values.ToDictionary(
            h => h.Id, h => (h.Name, h.Level, h.OwnerId));
        var receipts = store.ReceiptsByHero.Values
            .SelectMany(list => list)
            .DistinctBy(r => r.Id)
            .Where(r => r.UnixSeconds >= startUnix && r.UnixSeconds < endUnix);
        // The ladder is only the heroes that actually contested a ranked (staked) match this season —
        // LeaderboardBuilder lists every hero, so drop the idle ones and re-rank the survivors 1..N.
        var standings = Shared.LeaderboardBuilder.Build(heroes, receipts)
            .Where(e => e.Matches > 0)
            .Select((e, i) => e with { Rank = i + 1 })
            .ToList();
        return new Shared.SeasonLeaderboardDto(season.Number, endUnix, standings);
    }

    // ── Daily engagement loop ──────────────────────────────────────────────────────────────────
    // A once-per-UTC-day claim: a small base + a bonus per completed daily quest (server-verified,
    // derived from the receipt log), scaled by a login streak, paid from the treasury. Day/streak/
    // reward math is pure Core (Daily/DailyStreak/DailyReward); quests are Shared (DailyQuests).

    /// <summary>Today's daily-loop state for a player: the day's quests + which are done (from the
    /// player's in-window receipts), the projected streak, and what a claim right now would pay.</summary>
    public Shared.DailyStatusDto DailyStatus(Player player)
    {
        var window = Daily.ForDay(DateTimeOffset.UtcNow);
        var (heroIds, receipts) = DailyReceiptsInWindow(player, window);
        var quests = Shared.DailyQuests.ForDay(window.DayIndex, _config.DailyQuestsPerDay);

        var questDtos = quests.Select(q => new Shared.DailyQuestDto(
            q.Id, q.Title, _config.DailyQuestBonusSats,
            Shared.DailyQuests.IsComplete(q, receipts, heroIds))).ToList();

        var claimedToday = player.LastClaimDay == window.DayIndex;
        // The reward previews at the streak the claim will RESULT in (post-increment), so ClaimableNow
        // matches the payout; the displayed Streak is the player's CURRENT standing (0 when fresh, and
        // already-incremented once claimed today) — standard streak-counter semantics.
        var rewardStreak = claimedToday
            ? player.StreakCount
            : DailyStreak.Next(player.LastClaimDay, window.DayIndex, player.StreakCount);
        var reward = DailyReward.Compute(_config, questDtos.Count(q => q.Done), rewardStreak);

        return new Shared.DailyStatusDto(
            window.DayIndex, window.End.ToUnixTimeSeconds(), claimedToday, player.StreakCount,
            _config.DailyBaseSats, questDtos,
            ClaimableNowSats: claimedToday ? 0 : reward.Total,
            ProjectedSats: reward.Total);
    }

    /// <summary>Claim the daily reward: base + bonus per completed quest, streak-scaled, paid from the
    /// treasury. Once per UTC day; state is written only after the payout succeeds so a failed payout
    /// doesn't consume the day.</summary>
    public async Task<Shared.DailyClaimResultDto> ClaimDailyAsync(Player player, CancellationToken ct)
    {
        var window = Daily.ForDay(DateTimeOffset.UtcNow);
        if (player.LastClaimDay == window.DayIndex)
            throw new GameRuleException("Daily reward already claimed today.");

        var (heroIds, receipts) = DailyReceiptsInWindow(player, window);
        var quests = Shared.DailyQuests.ForDay(window.DayIndex, _config.DailyQuestsPerDay);
        var completed = quests.Where(q => Shared.DailyQuests.IsComplete(q, receipts, heroIds)).ToList();

        var newStreak = DailyStreak.Next(player.LastClaimDay, window.DayIndex, player.StreakCount);
        var reward = DailyReward.Compute(_config, completed.Count, newStreak);

        await chain.PayoutAsync(player.Id, reward.Total, $"daily:{window.DayIndex}", ct);

        player.LastClaimDay = window.DayIndex;   // consume the day only after the payout succeeds
        player.StreakCount = newStreak;

        return new Shared.DailyClaimResultDto(
            reward.Total, newStreak, reward.Base, reward.QuestBonus, reward.StreakBonusPct,
            completed.Select(q => q.Id).ToList());
    }

    /// <summary>The player's heroes' receipts falling inside a day window, plus the hero-id set.</summary>
    private (HashSet<string> HeroIds, List<Shared.ProgressionReceiptDto> Receipts) DailyReceiptsInWindow(
        Player player, DailyWindow window)
    {
        var heroIds = store.Heroes.Values.Where(h => h.OwnerId == player.Id).Select(h => h.Id).ToHashSet();
        var startUnix = window.Start.ToUnixTimeSeconds();
        var endUnix = window.End.ToUnixTimeSeconds();
        var receipts = store.ReceiptsByHero
            .Where(kv => heroIds.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .Where(r => r.UnixSeconds >= startUnix && r.UnixSeconds < endUnix)
            .DistinctBy(r => r.Id)
            .ToList();
        return (heroIds, receipts);
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
        var result = BattleEngine.Fight(challenger, defender, entropy, _config);

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
        session.ChallengerSnapshot = challengerSnapshot;   // persist the fight-time snapshots for spectator replay
        session.DefenderSnapshot = defenderSnapshot;
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
        // Friendly (unstaked) fights are practice: they carry no XP and must NOT feed the
        // ranked leaderboard (else a lone player could farm free wins to #1). Tag them so
        // LeaderboardBuilder — which counts only "match" receipts — ignores them.
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                session.WagerSats > 0 ? "match" : "friendly", session.Id, challenger.Id, defender.Id, result.WinnerId,
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

    private void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award, _config);
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

    /// <summary>
    /// The catalog item ids the player currently holds at least one unit of — the shop marks these
    /// as owned. Ownership is on-chain (the same balance the equip check reads), so it survives
    /// across sessions with no server-side inventory bookkeeping.
    /// </summary>
    public async Task<List<string>> OwnedItemIdsAsync(Player player, CancellationToken ct)
    {
        var owned = new List<string>();
        foreach (var item in Core.Equipment.ItemCatalog.All)
            if (await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct) > 0)
                owned.Add(item.Id);
        return owned;
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

    /// <summary>Recently sold (closed) offers — the marketplace's "just changed hands" strip.</summary>
    public async Task<IReadOnlyList<OfferListing>> ListSoldOffersAsync(int take, CancellationToken ct)
    {
        // Reconcile still-active offers first, so one that just sold surfaces here immediately.
        foreach (var offer in store.Offers.Values.Where(o => o.Status == "active").ToList())
            await ReconcileOfferAsync(offer, ct);
        return store.Offers.Values.Where(o => o.Status == "closed")
            .OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 24)).ToList();
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
