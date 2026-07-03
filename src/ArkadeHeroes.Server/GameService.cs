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

    // ── Matches: open then fight ───────────────────────────────────────

    public MatchSession OpenMatch(Player player, string challengerHeroId, string defenderHeroId)
    {
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot fight itself.");

        var seed = CommitReveal.NewSeed();
        var session = new MatchSession
        {
            Id = NewId("match"),
            ChallengerPlayerId = player.Id,
            ChallengerHeroId = challenger.Id,
            DefenderHeroId = defender.Id,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
        };
        store.Matches[session.Id] = session;
        return session;
    }

    public (MatchSession Session, BattleResult Result, string ServerSeedHex, string EntropyHex,
        long ChallengerXp, long DefenderXp,
        Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot)
        Fight(Player player, string matchId, string nonce)
    {
        if (!store.Matches.TryGetValue(matchId, out var session) || session.ChallengerPlayerId != player.Id)
            throw new GameRuleException($"Unknown match '{matchId}'.");
        if (session.Status != "open") throw new GameRuleException("Match already resolved.");
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

        return (session, result,
            Convert.ToHexString(session.ServerSeed).ToLowerInvariant(),
            session.EntropyHex,
            challengerWon ? winnerAward : loserAward,
            challengerWon ? loserAward : winnerAward,
            challengerSnapshot, defenderSnapshot);
    }

    private static void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award);
        hero.Level = level;
        hero.Xp = xp;
    }

    // ── Equipment ──────────────────────────────────────────────────────

    public async Task<(Hero Hero, long Balance, string PaymentRef)> BuyAndEquipAsync(
        Player player, string heroId, string itemId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        var paymentRef = await chain.PayFeeAsync(player.Id, item.PriceSats, $"item:{itemId}:{heroId}", ct);
        hero.Equipment.Equip(item);

        var balance = await chain.GetBalanceSatsAsync(player.Id, ct);
        return (hero, balance, paymentRef);
    }
}
