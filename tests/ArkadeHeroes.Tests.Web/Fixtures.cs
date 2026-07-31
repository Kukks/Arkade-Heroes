using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Minimal, valid DTOs. Only the fields these tests actually read are interesting; the rest exist
/// because the records require them, and are kept boring on purpose so a test's intent is its own asserts.</summary>
public static class Fixtures
{
    public static HeroDto Hero(string id, string name, string ownerId = "player-1", int level = 3) => new(
        Id: id,
        Name: name,
        OwnerId: ownerId,
        GenomeHex: "00112233445566778899aabbccddeeff",
        Generation: 0,
        Element: "Ember",
        Level: level,
        Xp: 0,
        XpToNext: 100,
        Stats: new StatsDto(100, 10, 10, 10, 10, 10, 5, 5),
        Skills: [],
        Equipment: new Dictionary<string, string>(),
        BreedCount: 0,
        BreedCooldownUntil: null,
        ParentAId: null,
        ParentBId: null,
        AssetId: null,
        MintArkTxId: null,
        Provenance: null,
        Rarity: new RarityDto("Common", 10, [], []));

    public static PlayerDto Player(string id = "player-1", string name = "tester", long balanceSats = 0) => new(
        PlayerId: id,
        Name: name,
        ArkadeAddress: "tark1qtestaddressfortestsonly0000000000000000",
        BalanceSats: balanceSats,
        StarterClaimed: false);

    public static DeathMatchDto DeathMatch(
        string id, string challengerHeroId, string defenderHeroId, string status, bool absorb = false) =>
        new(id, challengerHeroId, defenderHeroId, status, absorb, WinnerHeroId: null);

    /// <summary>Chain info carrying a real config, so a page that quotes a server-published price gets one.</summary>
    public static ChainInfoDto ChainInfo(long starterClaimFeeSats = 1_000) => new(
        Mode: "covenant",
        Network: "regtest",
        TreasuryAddress: "tark1qtreasury0000000000000000000000000000000",
        SpeciesAssetId: null,
        Config: Config(starterClaimFeeSats));

    public static GameConfigDto Config(long starterClaimFeeSats = 1_000) => new(
        AbsorbChance: 102,
        AbsorbContinueChance: 90,
        BreedingCooldownBaseSeconds: 3600,
        BreedingFeeSats: 1_000,
        MergeFeeSats: 1_000,
        MatchFeeBaseSats: 100,
        MatchFeePerLevel: 10,
        BreedFeeDoublingCap: 4,
        MatchmakingTake: 8,
        OfferListingFeeSats: 100,
        HeroRenameFeeSats: 500,
        TournamentRakePct: 5,
        InnateAbilities: false,
        StarterClaimFeeSats: starterClaimFeeSats);
}
