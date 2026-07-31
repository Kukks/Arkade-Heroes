using System.Text.RegularExpressions;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Source-reading guards over the Blazor pages, in the manner of <see cref="TechPageTests"/> and
/// <see cref="AccountPageTests"/>: the WASM client is outside this project's reference closure, so what can
/// be pinned is the SHAPE of a page, not what it renders.
///
/// <para>Each test here exists because the same defect has now been found by hand more than once, on
/// different pages, in the same form. They are deliberately about invariants a reviewer cannot hold in
/// their head across thirty files:</para>
///
/// <list type="bullet">
/// <item>a load-success flag that is set but never branched on, so "we couldn't read your roster" renders
/// as "you own nothing" — found on /duel, then again on /sell, /squad, /tournaments, /breed and /merge;</item>
/// <item>a set of status buckets with a combination that falls through all of them, so a real, staked,
/// paid-for thing vanishes from the UI — found on /duel (accepted+defender), then again on /squad,
/// /deathmatch and /tournaments;</item>
/// <item>copy that sends a stuck player to a page which cannot do the thing the copy promises.</item>
/// </list>
/// </summary>
public class PageStateContractTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }

    private static string WebDir(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "src", "ArkadeHeroes.Web" }.Concat(parts).ToArray());

    private static IEnumerable<(string Name, string Text)> Pages() =>
        Directory.EnumerateFiles(WebDir("Pages"), "*.razor")
            .Select(f => (Path.GetFileName(f)!, File.ReadAllText(f)));

    private static IEnumerable<(string Name, string Text)> Components() =>
        Directory.EnumerateFiles(WebDir("Components"), "*.razor")
            .Select(f => (Path.GetFileName(f)!, File.ReadAllText(f)));

    private static string Page(string name) => File.ReadAllText(WebDir("Pages", name));

    /// <summary>The markup half of a .razor file — everything before the @code block, which is where a
    /// state has to be branched on to be visible to a player.</summary>
    private static string Markup(string razor)
    {
        var at = razor.IndexOf("\n@code", StringComparison.Ordinal);
        return at < 0 ? razor : razor[..at];
    }

    // ── 1. A load-success flag must be branched on ──────────────────────────────

    /// <summary>
    /// "You have no heroes" and "we never managed to read your roster" are different facts. A page that
    /// tracks whether the read SUCCEEDED and then renders only on whether the list is EMPTY states the
    /// first while meaning either — which is how /duel once told a player with a full stable to go and
    /// claim starters, and how /sell, /squad, /tournaments, /breed and /merge all still did.
    ///
    /// <para>The flag being declared is the tell: nobody adds one by accident. If it is declared, the
    /// markup has to consult it.</para>
    /// </summary>
    [Fact]
    public void EveryPageThatTracksLoadSuccess_BranchesOnItInTheMarkup()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in Pages().Concat(Components()))
        {
            foreach (var flag in new[] { "_heroesLoaded", "_loaded", "_matchesLoaded" })
            {
                // Declared as a field at all?
                if (!Regex.IsMatch(text, $@"\b(bool\s+{flag}\b|{flag}\s*[,;])")) continue;
                if (!text.Contains(flag, StringComparison.Ordinal)) continue;
                if (!Markup(text).Contains(flag, StringComparison.Ordinal))
                    offenders.Add($"{name} sets {flag} but never renders on it");
            }
        }

        Assert.True(offenders.Count == 0,
            "A page that knows whether its load SUCCEEDED must say so, rather than letting a failed read "
            + "render as an empty account:\n  " + string.Join("\n  ", offenders)
            + "\n\nAdd an `else if (!<flag>)` branch above the `.Count == 0` one — see Duel.razor for the shape.");
    }

    /// <summary>
    /// The inverse of the test above, and the state the offending pages actually started in: a page whose
    /// empty-state makes a CLAIM ABOUT THE PLAYER'S ROSTER — "you need a hero", "you have no heroes to
    /// sell", "a squad needs 3 heroes (you have 0)" — must have a load-success flag to make that claim
    /// with. Without one there is nothing to branch on and the claim is simply asserted, true or not.
    ///
    /// <para>This catches the case test 1 cannot: a page with no flag at all.</para>
    /// </summary>
    [Fact]
    public void EveryPageThatClaimsSomethingAboutYourRoster_KnowsWhetherItReadIt()
    {
        var claims = new Regex(
            @"You need (a hero|at least)|You have no heroes|You don't (own|have) any heroes|needs 3 heroes",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();
        foreach (var (name, text) in Pages())
        {
            if (!claims.IsMatch(Markup(text))) continue;
            if (!Regex.IsMatch(text, @"_heroesLoaded|_loaded\b|\b_error\b"))
                offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These pages tell the player what they own without tracking whether the read succeeded, so a "
            + "failed load is stated as a fact about their account: " + string.Join(", ", offenders));
    }

    private static string Collapse(string s) =>
        Regex.Replace(s, @"\s+", " ") is { Length: > 140 } long_ ? long_[..140] + "…" : Regex.Replace(s, @"\s+", " ");

    // ── 2. Bucket coverage: nothing may fall through every filter ───────────────

    /// <summary>
    /// Every status the server can put on a match must be named by the page that lists those matches.
    ///
    /// <para>This is the bug that broke duels, and it broke them the same way four times. The page splits
    /// its list into buckets by (status × my role); a combination that matches no bucket does not render
    /// as "unknown", it renders as NOTHING — the row silently leaves the UI. A defender who accepted a duel
    /// saw their staked match disappear and concluded that accepting had done nothing. The same hole then
    /// swallowed accepted squad matches, settled death-matches (a hero burned forever, with no trace), and
    /// expired duels (a stranded covenant stake, invisible from the page that took it).</para>
    ///
    /// <para>Checking the status VOCABULARY is the cheap, durable half of the invariant: if the server
    /// grows a status, the page that lists it fails here rather than quietly dropping every row that
    /// carries it. The roles are then covered by the sibling test below.</para>
    /// </summary>
    [Theory]
    // page,                statuses the corresponding server list endpoint can emit
    [InlineData("Duel.razor", "open", "accepted", "resolved", "expired")]
    [InlineData("Squad.razor", "open", "accepted", "resolved")]
    [InlineData("DeathMatch.razor", "open", "accepted", "resolved")]
    [InlineData("Tournaments.razor", "open", "full", "resolved", "refunded")]
    public void AListingPage_NamesEveryStatusItsServerCanEmit(string page, params string[] statuses)
    {
        var text = Page(page);
        var missing = statuses.Where(s => !text.Contains($"\"{s}\"", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            $"{page} buckets rows by status and never mentions: {string.Join(", ", missing)}. "
            + "A row whose status matches no bucket does not render as unknown — it vanishes, along with "
            + "whatever the player staked into it. Add a bucket (and an empty-state that accounts for it), "
            + "or state in a comment why that status can never reach this page.");
    }

    /// <summary>
    /// Both sides of a two-party match must have somewhere to stand in every phase of it. A page that
    /// buckets "open + defender" and "accepted + challenger" but not "accepted + defender" erases the
    /// match for the player who just staked into it — which is exactly what happened on /duel, /squad and
    /// /deathmatch. Pinned by name because the bucket names are the design.
    /// </summary>
    [Theory]
    [InlineData("Duel.razor")]
    [InlineData("Squad.razor")]
    [InlineData("DeathMatch.razor")]
    public void ATwoSidedMatchPage_HasABucketForTheDefenderWhoAlreadyAccepted(string page)
    {
        var text = Page(page);
        Assert.True(text.Contains("AwaitingOpponent", StringComparison.Ordinal),
            $"{page} has no bucket for a match that is ACCEPTED with me as the defender. That is the state "
            + "every defender enters the instant they stake, and only the challenger can resolve it — so "
            + "without a (read-only) bucket the match disappears and accepting looks like a no-op.");
    }

    /// <summary>
    /// A page's "you have nothing" card must account for every bucket it renders. Adding a bucket without
    /// adding it to the empty-state guard produces the inverse bug: "No open matches" printed directly
    /// above the open matches.
    /// </summary>
    [Theory]
    [InlineData("Duel.razor", "No open matches")]
    [InlineData("Squad.razor", "No open squad matches")]
    [InlineData("DeathMatch.razor", "No open death-matches")]
    [InlineData("Tournaments.razor", "No open brackets")]
    public void TheEmptyStateGuard_MentionsEveryBucketThePageRenders(string page, string emptyCopy)
    {
        var text = Page(page);
        var markup = Markup(text);

        // Every `private IEnumerable<...> Name =>` bucket declared in the @code block.
        var buckets = Regex.Matches(text, @"private\s+IEnumerable<\w+>\s+(\w+)\s*=>")
            .Select(m => m.Groups[1].Value)
            .Where(n => markup.Contains(n + ".Any()", StringComparison.Ordinal)
                     || markup.Contains("in " + n, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(buckets);

        // The guard is the @if that wraps the empty-state copy.
        var guardLine = markup.Split('\n')
            .Select((l, i) => (Line: l, Index: i))
            .Where(x => x.Line.Contains(emptyCopy, StringComparison.Ordinal))
            .Select(x => string.Join('\n', markup.Split('\n').Take(x.Index)))
            .FirstOrDefault();
        Assert.NotNull(guardLine);

        // Take the last @if before the empty-state copy — that's the guard.
        var lastIf = guardLine!.LastIndexOf("@if (", StringComparison.Ordinal);
        Assert.True(lastIf >= 0, $"{page}: couldn't find the @if guarding \"{emptyCopy}\".");
        var guard = guardLine[lastIf..];

        var unguarded = buckets.Where(b => !guard.Contains(b + ".Any()", StringComparison.Ordinal)).ToList();
        Assert.True(unguarded.Count == 0,
            $"{page} renders {string.Join(", ", unguarded)} but its \"{emptyCopy}\" card does not check "
            + "them — so the page can claim you have nothing while listing the thing you have.");
    }

    // ── 3. Navigation that can actually do what the copy promises ───────────────

    /// <summary>Every route the app declares, so a link can be checked against reality.</summary>
    private static HashSet<string> Routes() =>
        Directory.EnumerateFiles(WebDir("Pages"), "*.razor")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"@page\s+""([^""]+)""").Select(m => m.Groups[1].Value))
            .Select(r => r.Trim('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A link with an EMPTY href resolves to the document you are already on. It reads as a call to action
    /// and does nothing — which is what the signed-out empty state on /heroes offered as its only way to
    /// start playing.
    /// </summary>
    [Fact]
    public void NoPageLinksToNowhere()
    {
        var offenders = Pages().Concat(Components())
            .Where(p => Regex.IsMatch(p.Text, @"<a\b[^>]*\bhref=""""") )
            .Select(p => p.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "href=\"\" navigates to the current page, so the link appears broken: "
            + string.Join(", ", offenders) + ". Use href=\"/\" for the landing page.");
    }

    /// <summary>
    /// Every static link target is a route that exists. Catches a page pointing at /ranks (the route is
    /// /leaderboard) or at a page that was renamed out from under it.
    /// </summary>
    [Fact]
    public void EveryStaticLinkTarget_IsARealRoute()
    {
        var routes = Routes();
        var offenders = new List<string>();

        foreach (var (name, text) in Pages().Concat(Components())
                     .Append(("MainLayout.razor", File.ReadAllText(WebDir("Layout", "MainLayout.razor")))))
        {
            foreach (Match m in Regex.Matches(text, @"href=""([^""@]*)"""))
            {
                var target = m.Groups[1].Value.Split('?', '#')[0].Trim('/');
                if (target.Length == 0) continue;                       // "/" or "" — covered above
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                // Parameterised routes are matched on their prefix (heroes/{Id}, watch/{MatchId}).
                if (routes.Contains(target)) continue;
                if (routes.Any(r => r.Contains('{') && target.StartsWith(r[..r.IndexOf('{')].Trim('/'),
                        StringComparison.OrdinalIgnoreCase))) continue;
                offenders.Add($"{name} → /{target}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These links point at routes that do not exist:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// "Claim your starters on the Heroes page" has to land somewhere that can sell you a hero. /heroes
    /// opens on the ALL tab and the recruit card renders only under Mine, so a bare href="heroes" put a
    /// stuck player in front of a grid of other people's creatures with no control anywhere on screen —
    /// a dead end at the exact moment they were told what to do about it.
    /// </summary>
    [Fact]
    public void CopyThatSendsYouToRecruit_LandsOnTheTabThatCanRecruit()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in Pages().Concat(Components()))
        {
            foreach (Match m in Regex.Matches(text, @"<a href=""heroes""[^>]*>[^<]*</a>"))
            {
                // The sentence around the link is what makes it a recruit instruction.
                var at = text.IndexOf(m.Value, StringComparison.Ordinal);
                var around = text.Substring(Math.Max(0, at - 220), Math.Min(320, text.Length - Math.Max(0, at - 220)));
                if (Regex.IsMatch(around, @"claim|recruit|need a hero|need at least", RegexOptions.IgnoreCase))
                    offenders.Add($"{name}: {Collapse(around)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Copy telling a player to get a hero must link to heroes?mine=1 — bare /heroes opens on the "
            + "All tab, where the recruit card does not render:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A route nothing links to is a feature nobody finds. /daily has a route, a section rule that lights
    /// the Heroes tab for it, and an entire streak-and-quests loop behind it — and for a while had no door
    /// at all, because it was dropped from the mode list on /play and never added to the subnav.
    /// </summary>
    [Fact]
    public void EveryPlayerFacingRoute_IsLinkedFromSomewhere()
    {
        // Operator/dev surfaces and terminal targets are reachable by design without a link.
        var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "", "admin", "studio", "not-found", "heroes/{Id}", "watch/{MatchId}" };

        var linkable = string.Concat(
            Pages().Select(p => p.Text)
                .Concat(Components().Select(c => c.Text))
                .Append(File.ReadAllText(WebDir("Layout", "MainLayout.razor"))));

        var unlinked = Routes()
            .Where(r => !exempt.Contains(r))
            .Where(r => !linkable.Contains($"href=\"{r}\"", StringComparison.Ordinal)
                     && !linkable.Contains($"href=\"{r}?", StringComparison.Ordinal)
                     && !linkable.Contains($"href=\"/{r}\"", StringComparison.Ordinal)
                     && !linkable.Contains($"\"{r}\", NavLinkMatch", StringComparison.Ordinal)
                     && !linkable.Contains($"\", \"{r}\", \"", StringComparison.Ordinal))
            .ToList();

        Assert.True(unlinked.Count == 0,
            "These routes exist but nothing navigates to them, so only someone typing the URL will ever "
            + "see them: " + string.Join(", ", unlinked));
    }

    // ── 4. A real-sats action states its price ─────────────────────────────────

    /// <summary>
    /// Every page that spends sats says what it costs, before the player commits.
    ///
    /// <para>Each entry below is an action that moves real bitcoin out of the wallet. /gauntlet said "a
    /// small entry fee applies" and never a number; /breed and /merge said "the fee" and never a number,
    /// on flows that respectively escalate to many multiples of the base and permanently destroy a hero;
    /// /duel and /squad quoted the wager and silently added a match fee on top. The price is available to
    /// all of them from the server's published config — see <see cref="PricingTests"/>.</para>
    /// </summary>
    [Theory]
    [InlineData("Gauntlet.razor", "Pricing.GauntletFee")]
    [InlineData("Breed.razor", "Pricing.BreedFee")]
    [InlineData("Merge.razor", "Pricing.MergeFee")]
    [InlineData("Duel.razor", "Pricing.MatchFee")]
    [InlineData("Squad.razor", "Pricing.MatchFee")]
    public void APageThatSpendsSats_QuotesThePriceFromTheServersConfig(string page, string call)
    {
        var text = Page(page);
        Assert.True(text.Contains(call, StringComparison.Ordinal),
            $"{page} spends real sats and must state the amount before the player commits, taken from the "
            + $"server's published config via {call}. A compiled-in number goes stale the moment an "
            + "operator retunes the fee, and goes stale while looking authoritative.");
    }

    /// <summary>
    /// A price is only quoted when it is KNOWN. <c>Config?.SomeFeeSats ?? 0</c> renders "0 sat" — i.e.
    /// tells the player the action is free — whenever the config read failed, which is reachable against
    /// any server that does not publish it. A confidently wrong price is worse than no price, because the
    /// player believes it. /home said "breed fee 0 sat" and /tournaments said "the house keeps a 0% rake".
    /// </summary>
    [Fact]
    public void NoPageRendersAnUnknownPriceAsZero()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in Pages().Concat(Components()))
        {
            // Only the MARKUP. `?? 0` in the @code block is ordinary defaulting and is often the safe
            // choice (a 0 that suppresses a fee line says nothing at all); the defect is specifically a
            // coalesced-to-zero fee reaching the screen as a printed price.
            foreach (Match m in Regex.Matches(Markup(text), @"Config\?\.\w+\s*\?\?\s*0"))
            {
                var at = Markup(text).IndexOf(m.Value, StringComparison.Ordinal);
                var line = Markup(text)[..at].LastIndexOf('\n') is var s && s >= 0
                    ? Markup(text)[(s + 1)..]
                    : Markup(text);
                var end = line.IndexOf('\n');
                line = end > 0 ? line[..end] : line;
                offenders.Add($"{name}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "An unread fee must not be rendered as zero — say nothing rather than saying \"free\":\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 5. A spend cannot be double-submitted ──────────────────────────────────

    /// <summary>
    /// The <c>disabled="@_busy"</c> attribute only takes effect at the next render, which happens at the
    /// handler's first yielding await — so a second click dispatched in that window runs the whole action
    /// again. On /tournaments that meant hosting a second bracket and paying a second real-sats buy-in.
    /// The guard has to be claimed BEFORE the first await, which is what Home.razor and DailyCard do.
    /// </summary>
    [Theory]
    [InlineData("Tournaments.razor", "_busy")]
    [InlineData("Duel.razor", "_busy")]
    [InlineData("Squad.razor", "_busy")]
    [InlineData("DeathMatch.razor", "_busy")]
    public void ASharedActionWrapperThatSpendsSats_ClaimsItsGuardBeforeTheFirstAwait(string page, string flag)
    {
        var text = Page(page);
        var wrapper = Regex.Match(text, @"async Task RunAction\([^)]*\)\s*\{(?<body>[\s\S]*?)\n    \}");
        Assert.True(wrapper.Success, $"{page}: expected a RunAction wrapper to guard.");

        // Comments first: these wrappers explain themselves, and the prose says "await" long before any
        // code does. Scanning the raw text made a correctly-guarded page look unguarded.
        var body = Regex.Replace(wrapper.Groups["body"].Value, @"//[^\n]*|/\*[\s\S]*?\*/", "");
        var guardAt = body.IndexOf($"if ({flag}) return;", StringComparison.Ordinal);
        var awaitAt = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(guardAt >= 0 && (awaitAt < 0 || guardAt < awaitAt),
            $"{page}: RunAction must start with `if ({flag}) return;`. Without it the disabled attribute is "
            + "the only protection, and it lands a render too late to stop a double-click from paying twice.");
    }

    // ── 6. An expression glued to a letter must be parenthesised ───────────────

    /// <summary>
    /// Razor treats <c>word@word.word</c> as an EMAIL ADDRESS and emits it verbatim instead of evaluating
    /// it. So <c>L@o.Hero.Level</c> renders as the literal text "L@o.Hero.Level", and it compiles clean —
    /// there is no warning, no build error, and no unit test that would notice, because the markup is
    /// valid. Only a human looking at the running page sees it.
    ///
    /// <para>This has now shipped twice. #124 found it on six pages at once (hero levels, as
    /// <c>L@o.Hero.Level</c>); it came back on the dungeon crawl as <c>W@rung.Wave</c>, where every rung of
    /// the shaft read "W@rung.Wave" instead of its depth. Both were caught by eye in a browser, which is
    /// exactly the review that does not happen reliably.</para>
    ///
    /// <para>The rule is the fix both times: an expression that follows a letter gets parentheses —
    /// <c>L@(o.Hero.Level)</c>, <c>W@(rung.Wave)</c>. That is unambiguous to the parser and reads no worse.
    /// This scans the markup of every page AND component, since the crawl lives in Components.</para>
    /// </summary>
    [Fact]
    public void NoExpressionIsGluedToALetterWhereRazorWouldReadItAsAnEmailAddress()
    {
        // The shape Razor's email heuristic swallows: an identifier character, then @, then a dotted
        // identifier chain. Razor only applies it when the character BEFORE the @ is part of a word, which
        // is why `ghost L@(wv.GhostLevel)` is safe once parenthesised and `> @foo.Bar` was never at risk.
        var suspect = new Regex(@"(?<![@\w.])\w@[A-Za-z_]\w*(\.\w+)+");
        var offenders = new List<string>();

        foreach (var (name, text) in Pages().Concat(Components()))
        {
            // Razor comments and email addresses in prose are not rendered expressions.
            var markup = Regex.Replace(Markup(text), @"@\*[\s\S]*?\*@", "");
            foreach (Match m in suspect.Matches(markup))
                offenders.Add($"{name}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "Razor will render these verbatim as email addresses instead of evaluating them — "
            + "parenthesise the expression (L@(o.Hero.Level)):\n  " + string.Join("\n  ", offenders));
    }
}
