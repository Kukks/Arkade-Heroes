using System.Text.RegularExpressions;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The pages make claims of FACT — what a thing costs, how many of something you get, what a rule is —
/// and this game's whole pitch is that a claim can be checked. A page that misstates a cost is therefore
/// not a typo; it is the product failing at the thing it exists to do.
///
/// <para>These read the page sources back and check each claim against the code that decides it, the way
/// <see cref="TechPageTests"/> does for opcodes and <see cref="CodexReferenceTests"/> does for rarity.
/// Every one of them is here because the claim it pins was WRONG in a shipped build: /play labelled the
/// Gauntlet "free" while it charged a level-scaled entry fee, the landing page promised "your first two
/// heroes" long after a claim minted one, Spar told players a hero may face itself while the button that
/// enforced the removed ban was still greyed out, /duel said an underdog risks nothing at a gap where it
/// still loses XP, and /achievements said no badge can be taken away while every badge but one is
/// recomputed from the roster you hold right now.</para>
/// </summary>
public class PageClaimTests
{
    // ── The /play stake chips: "free" has to mean free ──────────────────────────

    /// <summary>
    /// What entering each mode actually costs, from the code that prices it. A mode the page labels
    /// <c>Free</c> must cost nothing here, and a mode that costs anything must not be labelled Free —
    /// which is exactly the check that was missing when Gauntlet shipped with a Free chip on it.
    /// </summary>
    private static readonly Dictionary<string, Func<long>> EntryCost = new()
    {
        // Resolved in the browser against heroes you already own — no server call, no invoice.
        ["spar"] = () => 0,
        // Trials awards no XP, gear or sats, so there is nothing to farm and nothing to charge for.
        ["trials"] = () => 0,
        // Browsing surfaces. Buying gear costs sats; opening the catalogue to look does not.
        ["gear"] = () => 0,
        ["heroes"] = () => 0,
        ["achievements"] = () => 0,
        ["leaderboard"] = () => 0,
        ["reclaim"] = () => 0,
        // The daily claim PAYS the player; opening the page costs nothing. (The server may pay 0 from a
        // drained treasury, but that is a shortfall on a reward, not an entry cost.)
        ["daily"] = () => 0,

        // A same-level match fee plus the dungeon's entry premium. THE regression this test exists for.
        ["gauntlet"] = () => Gauntlet.Fee(1),
        // Both sides pay a per-character match fee on top of the wager they stake.
        ["duel"] = () => Leveling.MatchFee(1),
        ["squad"] = () => Leveling.MatchFee(1),
        // Permadeath is a multiple of the match fee — and absorb mode costs more still.
        ["deathmatch"] = () => Leveling.DeathMatchFee(1, absorb: false),
        // A first breed, at the floor of the escalating fee.
        ["breed"] = () => BreedingPolicy.FeeSats(GameConfig.Default.BreedingFeeSats, 0),
        // Fusing burns a hero AND charges a flat fee. The chip says "permanent"; the card says the fee.
        ["merge"] = () => GameConfig.Default.MergeFeeSats,
        // The buy-in is the host's own number; the page will not open a bracket below TournamentBuyInFloor.
        ["tournaments"] = () => TournamentBuyInFloor,
    };

    /// <summary>
    /// The smallest buy-in /tournaments will host — a bracket funded by nothing is not a bracket. The one
    /// entry above that is not computed from Core, because the buy-in is the host's own number; grounded
    /// instead by <see cref="TheTournamentsPage_PublishesTheRealPodiumSplit"/>, which checks the page still
    /// refuses a zero. Without that, a tournament that genuinely became free would leave this table
    /// demanding the chip keep saying "costs sats".
    /// </summary>
    private const long TournamentBuyInFloor = 1;

    /// <summary>Every (href, stake-chip) row in the /play mode table.</summary>
    private static (string Name, string Href, string Stake)[] PlayModes() =>
        Regex.Matches(
                Page("Play.razor"),
                """new\("(?<name>[^"]+)",\s*"(?<href>[^"]+)",\s*"[^"]*",.*?,\s*(?<stake>Free|Sats|Gone)\)""",
                RegexOptions.Singleline)
            // Priced by ROUTE, not by the exact link. A mode may point at a tab or a filter
            // ("heroes?mine=1" — the roster tab that can actually recruit); the query selects a view of
            // the same page and cannot change what entering it costs. Stripping it keeps the table keyed
            // on the thing that has a price, and stops a link gaining a query from reading as an
            // unpriced new mode.
            .Select(m => (m.Groups["name"].Value,
                          m.Groups["href"].Value.Split('?')[0],
                          m.Groups["stake"].Value))
            .ToArray();

    [Fact]
    public void EveryModeThePlayPageCallsFree_CostsNothingToEnter()
    {
        var modes = PlayModes();
        Assert.NotEmpty(modes);

        foreach (var (name, href, stake) in modes)
        {
            Assert.True(EntryCost.TryGetValue(href, out var cost),
                $"/play offers '{name}' at /{href}, but nothing here prices it. A mode reaches players "
                + "with a stake chip on it, so add its real entry cost to EntryCost — do not let a new "
                + "mode ship with a chip nobody checked.");

            var sats = cost();
            if (stake == "Free")
                Assert.True(sats == 0,
                    $"/play labels '{name}' FREE, but entering it costs {sats} sats. Free means nothing "
                    + "leaves your wallet. Fix the chip, not this test — this is the exact bug the /play "
                    + "redesign existed to prevent, and it shipped anyway.");
            else
                Assert.True(sats > 0,
                    $"/play charges '{name}' a stake, but its configured cost is {sats}. Either it really "
                    + "is free now — in which case say so — or the price this test reads is the wrong one.");
        }
    }

    [Fact]
    public void ThePlayPage_PricesTheGauntletAndSaysSo()
    {
        // Named on its own because it is the one that shipped wrong. The chip is checked above; this
        // pins that the card TEXT says the fee scales, which is the part a player actually reads.
        var gauntlet = Assert.Single(PlayModes(), m => m.Href == "gauntlet");
        Assert.Equal("Sats", gauntlet.Stake);
        Assert.True(Gauntlet.Fee(10) > Gauntlet.Fee(1),
            "The card says entry is 'scaled to the hero's level'. If the fee stopped scaling, that line "
            + "is now a claim about a behaviour the game no longer has.");
        Assert.Contains("scaled to the hero's level", Page("Play.razor"));
    }

    [Fact]
    public void ThePlayPage_DoesNotHideTheFusionFeeBehindThePermanentChip()
    {
        // "Permanent" is the loudest chip so it wins the label, but Fuse ALSO charges MergeFeeSats. A
        // card that mentions only the burned hero understates what the action takes.
        Assert.True(GameConfig.Default.MergeFeeSats > 0,
            "Fusing is free now — if that is deliberate, drop the fee sentence from the /play card and "
            + "from /merge rather than leaving copy that charges players in prose.");
        var fuse = Assert.Single(PlayModes(), m => m.Href == "merge");
        Assert.Equal("Gone", fuse.Stake);
        Assert.Contains("costs a fee", Page("Play.razor"));
    }

    [Fact]
    public void ThePlayPage_DoesNotHideTheDeathMatchFeeBehindThePermanentChip()
    {
        // The same defect the fusion card had, on the costlier action: a death-match charges BOTH sides
        // Leveling.DeathMatchFee and the card named only the burned hero.
        var config = GameConfig.Default;
        Assert.True(Leveling.DeathMatchFee(5, absorb: false, config) > Leveling.MatchFee(5, config),
            "A death-match costs more than a wager match; if that stops being true, retune the card's "
            + "\"steeper\" wording rather than leaving copy that overstates the charge.");
        Assert.True(Leveling.DeathMatchFee(5, absorb: true, config)
                    > Leveling.DeathMatchFee(5, absorb: false, config),
            "The card says absorb mode is steeper.");

        var dm = Assert.Single(PlayModes(), m => m.Href == "deathmatch");
        Assert.Equal("Gone", dm.Stake);
        Assert.Contains("Both sides also pay", Page("Play.razor"));
    }

    [Fact]
    public void TheMergePage_SaysTheFusionFeeLeavesYourWallet()
    {
        // /breed always said "both heroes + the fee". /merge said only "deposits both into a covenant
        // escrow" — on the page where the spend is actually committed.
        Assert.True(GameConfig.Default.MergeFeeSats > 0);
        Assert.Contains("the fusion fee", Page("Merge.razor"));
    }

    // ── How many heroes pressing Play actually buys ─────────────────────────────

    [Fact]
    public void TheLandingPage_DerivesTheStarterCount_RatherThanWritingItOut()
    {
        var home = Visible(Page("Home.razor"));

        // The count must come from the constant the server loops over, not from prose. It said "your
        // first two heroes" for as long as a claim minted a pair and kept saying it after the claim
        // became one — a promise of twice what the first real spend delivers.
        Assert.Contains("StarterPolicy.HeroCount", home);
        Assert.DoesNotContain("your first two heroes", home);

        // And the derived phrasing has to agree with today's constant, so a change to HeroCount that
        // leaves the singular/plural branch wrong is caught too.
        Assert.Equal(1, StarterPolicy.HeroCount);
        Assert.Contains("your first hero is summoned", home);
    }

    // ── Spar: a hero may face itself, and the page must actually let it ─────────

    [Fact]
    public void TheSparPage_DoesNotBanTheMirrorMatchItAdvertises()
    {
        var spar = Visible(Page("Spar.razor"));

        // The rule has nothing behind it — the engine resolves a mirror match like any other bout, which
        // is what #198 established and MirrorMatchTests keeps true. Proven here rather than assumed, so
        // this test cannot go green by the ban coming back to the ENGINE instead.
        var twin = new ArkadeHeroes.Core.Heroes.Hero
        {
            Id = "mirror", OwnerId = "p", Name = "Mirror",
            Genome = Genome.NewGen0("page-claim-mirror"u8.ToArray()), Level = 5,
        };
        var resolved = BattleEngine.Fight(twin, twin, System.Security.Cryptography.SHA256.HashData("m"u8));
        Assert.False(string.IsNullOrEmpty(resolved.WinnerId));

        // So the page must not re-assert it. #198 rewrote the copy and left the guard, which put the new
        // rule on screen in the one state where the button refused to honour it.
        Assert.Contains("a hero may face itself", spar);
        Assert.DoesNotContain("_aId == _bId)\"", spar);   // the disabled= expression
        Assert.DoesNotContain("_b is null || _aId == _bId", spar);   // the early return in DoFight
    }

    [Fact]
    public void TheSparPage_GivesAMirrorMatchTwoDistinctFighterIds()
    {
        // BattleArena reads every event's ActorId/TargetId against A.Id to pick a corner, and names the
        // winner the same way. Two corners sharing an id replays the bout as red hitting blue and crowns
        // red every time — so allowing the mirror match without this would trade a refused fight for a
        // misreported one.
        Assert.Contains("MirrorId", Page("Spar.razor"));
        Assert.Contains("ev.ActorId == A!.Id", Component("BattleArena.razor"));
        Assert.Contains("fight.WinnerId == A!.Id", Component("BattleArena.razor"));
    }

    // ── Duel: "underdog" and "risks nothing" are not the same set ───────────────

    [Fact]
    public void TheDuelPage_DoesNotPromiseEveryUnderdogAFreeShot()
    {
        // The witness: at exactly the gap where Matchmaking starts calling you an underdog, losing still
        // costs XP. The two thresholds are different numbers and always were.
        // Quoted against a hero solvent enough to settle the whole gap, so this stays a claim about the
        // two LEVEL thresholds — XpIfLose is now clamped to what the loser owns, and a broke hero would
        // read as zero for a reason that has nothing to do with the gap being measured here.
        const int me = 5, them = me + 3;
        const long solvent = 100_000;
        Assert.Equal("underdog", Matchmaking.Favor(me, them));
        Assert.True(Matchmaking.XpIfLose(me, them, solvent) > 0,
            "If an underdog by Favor's own definition now loses nothing, the strong claim would be true "
            + "again — and this test should be replaced by one that pins the two thresholds together.");

        var duel = Visible(Page("Duel.razor"));
        Assert.DoesNotContain("underdog risks nothing", duel);
        // The page's own honest test is the free-shot flag, which checks the XP directly.
        Assert.Contains("o.XpIfYouLose == 0", duel);
    }

    // ── Tournaments: the pot pays two, not three ────────────────────────────────

    /// <summary>
    /// /play told players a buy-in pot "splits across the top three". It never has.
    /// <see cref="Tournament.PrizeWeights"/> is [70, 30] and <see cref="Tournament.Podium"/> returns the
    /// champion and the final's loser, so there is no third place at any bracket size — a third entrant
    /// who believed the card was promised a cut of real sats that no code path can pay.
    ///
    /// <para>Pinned to the CONSTANT rather than to the words, so adding a third weight one day makes this
    /// fail loudly instead of leaving the copy quietly wrong in the other direction.</para>
    /// </summary>
    [Fact]
    public void ThePlayPage_StatesTheTournamentSplitThePrizeWeightsActuallyPay()
    {
        var play = Visible(Page("Play.razor"));

        Assert.Equal(2, Tournament.PrizeWeights.Count);
        Assert.Equal([70, 30], Tournament.PrizeWeights);

        // The lie, verbatim.
        Assert.DoesNotContain("top three", play);
        // And the truth, in the terms the weights set.
        Assert.Contains("champion and runner-up", play);
        Assert.Contains("70/30", play);
    }

    // The behavioural half — that a real bracket's podium is two deep — is already
    // TournamentTests.Podium_IsChampionThenRunnerUp, so it is not restated here.

    // ── Achievements: most badges are revocable ─────────────────────────────────

    [Fact]
    public void TheAchievementsPage_DoesNotClaimBadgesArePermanent()
    {
        // GameService.PlayerAchievements recomputes owned/bred/legendaries/fancies/tournamentsWon from
        // the CURRENT roster, so selling or burning a hero takes the badge with it. Only Trailblazer was
        // deliberately made permanent. AchievementsTests.ABadgeEarnedByOwning_IsLostWhenTheHeroLeaves
        // is the behavioural half of this pair.
        var page = Visible(Page("Achievements.razor"));
        Assert.DoesNotContain("nothing can be taken away", page);
        Assert.Contains("Trailblazer", page);
    }

    // ── Numbers written into pages by hand ──────────────────────────────────────

    [Fact]
    public void TheGauntletPage_PublishesTheRealXpCapAndWaveCount()
    {
        var page = Page("Gauntlet.razor");

        // Authored content (dungeons.json), now RENDERED rather than hand-written — so pin that they stay
        // live, and that no literal creeps back. The wave count also sat in the full-clear CHECK.
        Assert.Contains("Gauntlet.PveXpLevelCap", page);
        Assert.Contains("Gauntlet.WaveCount", page);
        Assert.DoesNotContain($"capped at level {Gauntlet.PveXpLevelCap}", page);
        Assert.DoesNotContain($"past level {Gauntlet.PveXpLevelCap}", page);
        Assert.DoesNotContain($"WavesCleared == {Gauntlet.WaveCount}", page);
        Assert.DoesNotContain($"{Gauntlet.WaveCount} escalating ghost waves", page);
        Assert.DoesNotContain("five waves down", page);
    }

    [Fact]
    public void TheDuelPage_SaysALossCanCostALevel()
    {
        // PayableTransfer takes from the BANKED total, not progress-within-level, so one upset loss really
        // does delevel — measured, a level-3 hero beaten by a level-1 drops to 2. The page explained the XP
        // transfer in detail and never mentioned levels, which is the consequence a player actually sees.
        Assert.Contains("costs a level", Visible(Page("Duel.razor")));
    }

    [Fact]
    public void TheGauntletPage_SaysTheLadderClimbsWithTheHero()
    {
        var page = Visible(Page("Gauntlet.razor"));

        // Gauntlet.GhostFor takes the RUNNER, so levelling raises the fee and the opposition together and
        // the odds barely move — measured flat from level 1 to 12 ungeared. A page that says the fee scales
        // with level and stays silent on the ghosts reads as the game cheating.
        Assert.Contains("climbs with you", page);
        Assert.Contains("Gear is the lever", page);
    }

    [Fact]
    public void TheDeathMatchPage_QuotesItsFeeBeforeTheIrreversibleCommit()
    {
        var page = Visible(Page("DeathMatch.razor"));

        // The only irreversible action, and the last quoting nothing. From the helper, at the confirm.
        Assert.Contains("Pricing.DeathMatchFee", page);
        Assert.Contains("PERMADEATH", page);
        Assert.Contains("entry fee", page);
    }

    [Fact]
    public void TheSquadPage_IsBuiltForExactlyTheLineupSize()
    {
        // Visible, not raw: a claim that only appears in a comment is not a claim the page makes.
        var page = Visible(Page("Squad.razor"));

        // The picker IS a loop, so its bound looks substitutable — but Slot(i) is a ternary chain ending in
        // _slot2, so a loop to 4 over three backing fields would render a fourth picker aliased to the third.
        Assert.True(SquadBattle.LineupSize == 3,
            $"SquadBattle.LineupSize moved to {SquadBattle.LineupSize}. Squad.razor is built for exactly 3 " +
            "— per-slot fields, per-slot pickers, copy, and its validation — and needs a real edit, not a number.");

        Assert.Contains($"A squad needs {SquadBattle.LineupSize} heroes", page);
        Assert.Contains($"Count() == {SquadBattle.LineupSize}", page);

        // EXACTLY that many. Asserting only that _slot0.._slot2 exist would pass a page that had quietly
        // grown a fourth — the direction this guard exists to catch.
        for (var slot = 0; slot < SquadBattle.LineupSize; slot++)
            Assert.Contains($"_slot{slot}", page);
        Assert.DoesNotContain($"_slot{SquadBattle.LineupSize}", page);
    }

    [Fact]
    public void TheTournamentsPage_PublishesTheRealPodiumSplit()
    {
        // The rake is read live from the server; the split is a Core constant written into the page.
        Assert.Equal(new[] { 70, 30 }, Tournament.PrizeWeights.ToArray());
        Assert.Contains($"split it {string.Join("/", Tournament.PrizeWeights)}", Page("Tournaments.razor"));
        // …and the rake must stay live rather than becoming a second hand-written number.
        Assert.Contains("Config?.TournamentRakePct", Page("Tournaments.razor"));
        // Grounds TournamentBuyInFloor above: the host button refuses a bracket funded by nothing, so
        // entering one always costs sats and the /play chip on it can never honestly read "free".
        Assert.Contains("_buyIn < 1", Page("Tournaments.razor"));
    }

    [Fact]
    public void TheDailyPage_PublishesTheRealStreakCeiling()
    {
        // "up to double" is the streak cap read out loud: +100% is ×2.
        Assert.Equal(100, GameConfig.Default.DailyStreakCapPct);
        Assert.Contains("up to double", Page("Daily.razor"));
    }

    [Fact]
    public void TheSellPage_QuotesTheServersListingFee_RatherThanACompiledInOne()
    {
        // The fee is baked into each offer's covenant at listing time, so a stale number here would
        // misstate what a seller nets on a sale they cannot renegotiate afterwards.
        var sell = Page("Sell.razor");
        Assert.Contains("_listingFeeSats", sell);
        Assert.Contains("Listing costs nothing", sell);
        // The seller absorbs it out of the ask — the page must show the net, not just the ask.
        Assert.Contains("_ask - _listingFeeSats", sell);
    }

    // ── Sources ─────────────────────────────────────────────────────────────────

    private static string Page(string name) => Read("Pages", name);
    private static string Component(string name) => Read("Components", name);

    /// <summary>
    /// A source with its razor comments stripped — what a player actually reads, plus the code behind it.
    /// These files routinely QUOTE the wrong copy they replaced, in a note explaining why it went; a test
    /// that forbids a phrase has to be looking at the markup rather than at its own epitaph.
    /// </summary>
    /// <summary>
    /// The page with everything a player CANNOT see taken out — both comment forms.
    ///
    /// <para>Razor <c>@* *@</c> blocks were always stripped. C# <c>//</c> lines are stripped too, because
    /// the convention in these pages is to record the wrong copy next to the fix that replaced it ("the
    /// top-three claim was not true", "'An underdog risks nothing' was false") — and a
    /// <c>DoesNotContain</c> that reads those comments fails on the very note explaining why the page is
    /// now right. Mode lists live inside <c>@code</c>, where <c>//</c> is the only comment form available,
    /// so this is not a rare case.</para>
    ///
    /// <para>Only comments that START a line, so a <c>//</c> inside a URL or a string literal is left
    /// alone — those are page content and a claim could genuinely hide in one.</para>
    /// </summary>
    private static string Visible(string source) =>
        Regex.Replace(
            Regex.Replace(source, @"@\*.*?\*@", "", RegexOptions.Singleline),
            @"^[ \t]*//.*$", "", RegexOptions.Multiline);

    private static string Read(string folder, string name)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", folder, name);
        if (!File.Exists(path)) throw new InvalidOperationException($"Expected {name} at {path}.");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }
}
