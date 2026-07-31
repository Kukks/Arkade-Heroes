using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The player's "my heroes" surface: /heroes recruits, /account shows what you own and what each hero
/// has been through.
///
/// <para>Two kinds of check live here, because the surface has two kinds of failure. The first half
/// exercises the SERVER contract /account renders — the match list is global and unscoped, so the page
/// has to attribute a fight to a hero by id, and getting that join wrong would show one player another
/// player's record. The second half reads the page sources back, the way <see cref="TechPageTests"/>
/// does, because the WASM client is outside this project's reference closure and the thing worth pinning
/// is WHERE an action lives, not what a component renders.</para>
/// </summary>
public class AccountPageTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    // ── The contract /account's per-hero history is built on ────────────────────

    /// <summary>
    /// GET /matches serves the arena's recent matches for EVERYONE, not the caller's own — so the join
    /// the page makes (hero id against ChallengerHeroId/DefenderHeroId) is the only thing keeping one
    /// player's history off another's page. This pins both halves: mine is found, theirs is excluded.
    /// </summary>
    [Fact]
    public async Task AMatchIsAttributableToTheHeroesThatFoughtIt_AndToNoOthers()
    {
        var (alice, _) = await factory.RegisterAsync($"acct-a-{Guid.NewGuid():N}");
        var (bob, _) = await factory.RegisterAsync($"acct-b-{Guid.NewGuid():N}");
        var (carol, _) = await factory.RegisterAsync($"acct-c-{Guid.NewGuid():N}");
        var aliceHero = (await alice.RecruitAsync(StarterPolicy.HeroCount))[0];
        var bobHero = (await bob.RecruitAsync(StarterPolicy.HeroCount))[0];
        var carolHero = (await carol.RecruitAsync(StarterPolicy.HeroCount))[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id));
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("acct-history"));

        var matches = await alice.Matches.ListAsync();
        var mine = Resolved(matches, aliceHero.Id);

        var m = Assert.Single(mine, x => x.MatchId == open.MatchId);
        Assert.Equal(aliceHero.Id, m.ChallengerHeroId);
        Assert.Equal(bobHero.Id, m.DefenderHeroId);
        // The won/lost chip reads WinnerId against the hero's own id, so the winner must be one of the two.
        Assert.Contains(m.Result!.WinnerId, new[] { aliceHero.Id, bobHero.Id });
        Assert.Equal(fight.Result.WinnerId, m.Result.WinnerId);

        // Carol never fought. A page that filtered by "my player" rather than "my hero" — or not at all —
        // would hand her Alice's duel, which is the failure this join exists to prevent.
        Assert.Empty(Resolved(await carol.Matches.ListAsync(), carolHero.Id));
    }

    /// <summary>
    /// An OPEN match is not history. The row carries a won/lost chip, so treating an unresolved match as
    /// past would print a verdict on a fight that has not happened — which is why the page filters on
    /// Result rather than on the hero ids alone.
    /// </summary>
    [Fact]
    public async Task AnUnresolvedMatch_IsNotYetHistory()
    {
        var (alice, _) = await factory.RegisterAsync($"acct-open-a-{Guid.NewGuid():N}");
        var (bob, _) = await factory.RegisterAsync($"acct-open-b-{Guid.NewGuid():N}");
        var aliceHero = (await alice.RecruitAsync(StarterPolicy.HeroCount))[0];
        var bobHero = (await bob.RecruitAsync(StarterPolicy.HeroCount))[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id));

        var listed = await alice.Matches.ListAsync();
        Assert.Contains(listed, x => x.MatchId == open.MatchId);              // it exists...
        Assert.Empty(Resolved(listed, aliceHero.Id));                          // ...but it is not a result

        await alice.Matches.FightAsync(open.MatchId, new FightRequest("acct-open"));
        Assert.Single(Resolved(await alice.Matches.ListAsync(), aliceHero.Id));
    }

    /// <summary>The page's own filter, kept next to the assertions that rely on it.</summary>
    private static List<MatchDto> Resolved(List<MatchDto> matches, string heroId) =>
        matches.Where(m => m.Result is not null
                        && (m.ChallengerHeroId == heroId || m.DefenderHeroId == heroId))
               .ToList();

    // ── Where the recruit action lives ──────────────────────────────────────────

    private static string Page(string name) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "Pages", name));

    private static string Layout(string name) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "Layout", name));

    /// <summary>
    /// Recruiting is a real spend, and it used to sit on /wallet — two clicks from the roster it fills,
    /// so the page that said "you have no heroes" could only point somewhere else. It now lives with the
    /// heroes, and in ONE place: two buttons quoting the same fee is two places to keep honest about
    /// what a hero costs, and the pair drifting is exactly how a stale price gets shipped.
    /// </summary>
    [Fact]
    public void ExactlyOnePage_BuysHeroes()
    {
        var buyers = Directory
            .EnumerateFiles(Path.Combine(FindRepoRoot(), "src", "ArkadeHeroes.Web", "Pages"), "*.razor")
            .Where(f => File.ReadAllText(f).Contains("ClaimStartersAsync"))
            .Select(f => Path.GetFileName(f)!)
            .ToList();

        Assert.True(buyers is ["Heroes.razor"],
            "The recruit action must live on the roster and nowhere else — found it on: "
            + (buyers.Count == 0 ? "no page at all" : string.Join(", ", buyers))
            + ". If recruiting genuinely belongs somewhere new, move it; do not add a second copy of a "
            + "button that quotes a fee and spends real sats.");
    }

    /// <summary>
    /// The button states the price before the player commits, and takes it from the SERVER's published
    /// config. A compiled-in number would be wrong the moment an operator retunes the breed fee the
    /// claim price is derived from — and it would be wrong while looking authoritative.
    /// </summary>
    [Fact]
    public void TheRecruitButton_QuotesTheServersPrice_AndSaysWhenYouCannotAffordIt()
    {
        var roster = Page("Heroes.razor");

        Assert.Contains("Config?.StarterClaimFeeSats", roster);
        // Reading the price must stay a plain GET: quoting a fee is not a reason to create an invoice.
        Assert.Contains("Chain.InfoAsync()", roster);
        Assert.DoesNotContain("RequestStartersAsync", roster);
        // "You have N sats; recruiting costs M" — a disabled button with no reason on it reads as broken.
        Assert.Contains("recruiting costs", roster);
    }

    /// <summary>
    /// A new route is only real if something links to it. /account is reachable two ways — the heroes
    /// subnav lists it, and the section map lights the Heroes tab while you are on it — and a page you
    /// can only reach by typing the URL is a page nobody sees.
    /// </summary>
    [Fact]
    public void TheAccountPage_IsRoutedAndReachable()
    {
        Assert.Contains("@page \"/account\"", Page("Account.razor"));

        var layout = Layout("MainLayout.razor");
        Assert.Contains("(\"Account\", \"account\"", layout);
        // The section map keeps the parent nav item lit; without it /account reads as "no section at all".
        Assert.Contains("or \"account\" => \"heroes\"", layout);
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
