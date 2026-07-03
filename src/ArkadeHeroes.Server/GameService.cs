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
/// Orchestrates game flows: registration, starter mints, two-phase
/// commit–reveal breeding and matches, purchases. Core stays pure; the chain
/// service anchors heroes and fees on Arkade.
/// </summary>
public class GameService(GameStore store, IChainService chain, IOptions<GameOptions> options)
{
    private readonly GameOptions _options = options.Value;

    private static string NewId(string prefix)
        => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    // ── Players ────────────────────────────────────────────────────────

    public async Task<(Player Player, PlayerWallet Wallet, long Balance)> RegisterPlayerAsync(
        string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new GameRuleException("Player name is required.");
        var player = new Player
        {
            Id = NewId("player"),
            Name = name.Trim(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        };
        store.Players[player.Id] = player;
        store.PlayersByToken[player.Token] = player;
        var wallet = await chain.GetOrCreatePlayerWalletAsync(player.Id, ct);
        var balance = await chain.GetBalanceSatsAsync(player.Id, ct);
        return (player, wallet, balance);
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

    /// <summary>Mints the one-time pair of generation-0 starter heroes.</summary>
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

    // ── Breeding: commit then reveal ───────────────────────────────────

    public async Task<(BreedingSession Session, long Fee)> CommitBreedingAsync(
        Player player, string parentAId, string parentBId, CancellationToken ct)
    {
        var parentA = GetOwnedHero(player, parentAId);
        var parentB = GetOwnedHero(player, parentBId);

        if (BreedingService.Validate(parentA, parentB, DateTimeOffset.UtcNow) is { } error)
            throw new GameRuleException(error);

        // Fee up front: committing reserves the server seed.
        await chain.PayFeeAsync(player.Id, _options.BreedingFeeSats, $"breed:{parentAId}+{parentBId}", ct);

        var seed = CommitReveal.NewSeed();
        var session = new BreedingSession
        {
            Id = NewId("breed"),
            PlayerId = player.Id,
            ParentAId = parentAId,
            ParentBId = parentBId,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
        };
        store.Breedings[session.Id] = session;
        return (session, _options.BreedingFeeSats);
    }

    public async Task<(Hero Child, string ServerSeedHex, string EntropyHex)> RevealBreedingAsync(
        Player player, string breedingId, string nonce, CancellationToken ct)
    {
        if (!store.Breedings.TryGetValue(breedingId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown breeding session '{breedingId}'.");
        if (session.Completed) throw new GameRuleException("Breeding already completed.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

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

        var child = await MintHeroAsync(player, outcome.ChildGenome, outcome.ChildGeneration,
            session.ParentAId, session.ParentBId, serverSeedHex, nonce, entropyHex, ct);
        session.ChildHeroId = child.Id;

        return (child, serverSeedHex, entropyHex);
    }

    // ── Matches: open (→ accept when wagered) → fight ──────────────────

    public async Task<MatchSession> OpenMatchAsync(
        Player player, string challengerHeroId, string defenderHeroId, long wagerSats, CancellationToken ct)
    {
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot fight itself.");
        if (wagerSats < 0)
            throw new GameRuleException("Wager cannot be negative.");
        if (wagerSats > 0 && defender.OwnerId == player.Id)
            throw new GameRuleException("Wagered matches need an opponent — you own both heroes.");

        // Challenger escrows their stake with the treasury up front.
        if (wagerSats > 0)
            await chain.PayFeeAsync(player.Id, wagerSats, $"wager-stake:challenger", ct);

        var seed = CommitReveal.NewSeed();
        var session = new MatchSession
        {
            Id = NewId("match"),
            ChallengerPlayerId = player.Id,
            ChallengerHeroId = challenger.Id,
            DefenderHeroId = defender.Id,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
            WagerSats = wagerSats,
            DefenderPlayerId = defender.OwnerId,
        };
        store.Matches[session.Id] = session;
        return session;
    }

    /// <summary>Defender's owner accepts a wagered match by escrowing the matching stake.</summary>
    public async Task<MatchSession> AcceptMatchAsync(Player player, string matchId, CancellationToken ct)
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

        await chain.PayFeeAsync(player.Id, session.WagerSats, $"wager-stake:defender:{matchId}", ct);
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return session;
    }

    public async Task<(MatchSession Session, BattleResult Result, string ServerSeedHex, string EntropyHex,
        long ChallengerXp, long DefenderXp,
        Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot, long WinnerPayout)>
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

        session.Status = "resolved";
        session.Result = result;
        session.Nonce = nonce;
        session.EntropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // Wager settlement: winner's owner takes the whole pot from escrow.
        long winnerPayout = 0;
        if (session.WagerSats > 0)
        {
            winnerPayout = session.WagerSats * 2;
            var winnerOwnerId = challengerWon ? session.ChallengerPlayerId : session.DefenderPlayerId!;
            await chain.PayoutAsync(winnerOwnerId, winnerPayout, $"wager-pot:{session.Id}", ct);
        }

        return (session, result,
            Convert.ToHexString(session.ServerSeed).ToLowerInvariant(),
            session.EntropyHex,
            challengerWon ? winnerAward : loserAward,
            challengerWon ? loserAward : winnerAward,
            challengerSnapshot, defenderSnapshot, winnerPayout);
    }

    // ── Hero transfer ──────────────────────────────────────────────────

    public async Task<(Hero Hero, string ArkTxId)> TransferHeroAsync(
        Player player, string heroId, string toPlayerId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        if (toPlayerId == player.Id)
            throw new GameRuleException("Hero already belongs to you.");
        if (!store.Players.ContainsKey(toPlayerId))
            throw new GameRuleException($"Unknown player '{toPlayerId}'.");

        var arkTxId = await chain.TransferHeroAssetAsync(player.Id, toPlayerId, hero.AssetId ?? hero.Id, ct);

        // Item assets stay in the sender's wallet, so the loadout can't travel:
        // strip it, returning the units to the sender's equip pool.
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);

        hero.OwnerId = toPlayerId;
        return (hero, arkTxId);
    }

    private static void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award);
        hero.Level = level;
        hero.Xp = xp;
    }

    // ── Equipment ──────────────────────────────────────────────────────
    // Buying pays the price and delivers a unit of the item's fungible Arkade
    // asset; equipping allocates a held unit to a hero. A unit can back only
    // one equipped hero at a time.

    public async Task<(string ItemAssetId, string ArkTxId, long Balance, ulong UnitsHeld)> BuyItemAsync(
        Player player, string itemId, CancellationToken ct)
    {
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        await chain.PayFeeAsync(player.Id, item.PriceSats, $"item:{itemId}", ct);
        var delivery = await chain.DeliverItemAssetAsync(player.Id, item.Id, item.Name, ct);

        var balance = await chain.GetBalanceSatsAsync(player.Id, ct);
        var held = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
        return (delivery.ItemAssetId, delivery.ArkTxId, balance, held);
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
        // Re-equipping the same item on the same hero's slot is a no-op allocation-wise.
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
