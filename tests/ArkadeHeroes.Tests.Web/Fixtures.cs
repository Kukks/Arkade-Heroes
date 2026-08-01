using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Minimal, valid DTOs. Only the fields these tests actually read are interesting; the rest exist
/// because the records require them, and are kept boring on purpose so a test's intent is its own asserts.</summary>
public static class Fixtures
{
    /// <summary>
    /// A REAL 32-byte genome — the one thing in this file that cannot be boring, because it is the only
    /// field the pages PARSE rather than print.
    ///
    /// <para>It used to be 16 bytes, and <see cref="ArkadeHeroes.Core.Genetics.Genome.FromHex"/> THROWS on a
    /// wrong length. Every call site swallows that (<c>HeroDetail</c> wraps both of its genome reads in a
    /// <c>try</c> and falls back to "no passives"/"no shape"; <c>Spar</c> turns it into a fight error), so the
    /// dependent markup simply never rendered and any test about it passed while asserting nothing. See
    /// <c>FixtureGenomeTests</c>, which fails loudly if this string stops parsing.</para>
    ///
    /// <para>The bytes are picked, not mashed — a genome of repeated or all-zero bytes is its own kind of
    /// unrepresentative:</para>
    /// <list type="bullet">
    /// <item>[0..4] stat and [8..12] growth genes are ten DISTINCT mid-to-high values, so the derived stats
    /// differ from each other and <c>CombatShapes.Of</c> lands on <b>Tempo</b> by a wide margin at every
    /// level — not on a tie. Ties fall to Offense, so a tied fixture would read Offense no matter what the
    /// classifier did. Their maximum (150) also puts <c>StatGeneCeiling</c> at 255, i.e. a full-range hero
    /// rather than something a capped recruit mint could have produced.</item>
    /// <item>[5] = 0x08, and 8 % 8 == 0 == <c>Element.Ember</c> — so the genome AGREES with the
    /// <c>Element: "Ember"</c> this same fixture declares below.</item>
    /// <item>[6]/[7] skill genes index 1 and 5 of the nine gene skills: Ember Lash (Attack-scaling, and the
    /// hero's own element) and Volt Surge (Magic-scaling). Two DIFFERENT skills, so the kit is a kit.</item>
    /// <item>[16..31] trait block: seven of the eight dominant genes are expressed and Aura's is deliberately
    /// plain (0), which is the branch <c>Traits.InnatePassives</c> skips — so the list it returns is both
    /// non-empty and not simply "all of them". Every dominant is below the 206 Uncommon cutoff, so the visible
    /// tier computes Common and agrees with the <c>Rarity</c> tier declared below. Both affinity categories
    /// carry a value, so <c>AffinityModifier</c> exercises its summing path.</item>
    /// </list>
    ///
    /// <para>The rest of <c>Rarity</c> (score, expressed list, recessive list) stays a stub: it is a summary
    /// the SERVER computes and these pages only print, so nothing cross-checks it against this genome.</para>
    /// </summary>
    public const string GenomeHex = "5a3c782896081c3240206010802a0d51002e1f004b637d1192cda5396684571a";

    public static HeroDto Hero(string id, string name, string ownerId = "player-1", int level = 3) => new(
        Id: id,
        Name: name,
        OwnerId: ownerId,
        GenomeHex: GenomeHex,
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
