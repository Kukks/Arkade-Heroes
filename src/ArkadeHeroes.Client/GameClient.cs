using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client;

public class GameClientException(string message) : Exception(message);

/// <summary>
/// Console game client. Deliberately minimal UI — the interesting part is that
/// every breed and fight is audited locally via <see cref="FairnessAudit"/>:
/// the client re-derives genomes and replays battles instead of trusting the
/// server's word.
/// </summary>
public class GameClient(string serverUrl)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(serverUrl) };
    private static readonly string SessionFile =
        Path.Combine(AppContext.BaseDirectory, "arkade-heroes-session.json");

    private PlayerDto? _me;
    private readonly List<HeroDto> _lastListing = [];

    // ── Session ────────────────────────────────────────────────────────

    public async Task TryResumeSessionAsync()
    {
        if (!File.Exists(SessionFile)) return;
        try
        {
            var session = JsonSerializer.Deserialize<SessionState>(await File.ReadAllTextAsync(SessionFile));
            if (session?.Token is null || session.ServerUrl != serverUrl) return;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            _me = await GetAsync<PlayerDto>("/api/players/me");
            Console.WriteLine($"  resumed session as {_me.Name} ({_me.BalanceSats} sats)\n");
        }
        catch
        {
            _http.DefaultRequestHeaders.Authorization = null;
            _me = null;
        }
    }

    private async Task SaveSessionAsync(string token)
        => await File.WriteAllTextAsync(SessionFile,
            JsonSerializer.Serialize(new SessionState(serverUrl, token)));

    private record SessionState(string ServerUrl, string Token);

    // ── Command dispatch ───────────────────────────────────────────────

    public async Task<bool> ExecuteAsync(string[] parts)
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "help": PrintHelp(); break;
            case "register": await RegisterAsync(Arg(parts, 1, "register <name>")); break;
            case "me": await ShowMeAsync(); break;
            case "starter": await ClaimStarterAsync(); break;
            case "mine": await ListHeroesAsync(mineOnly: true); break;
            case "heroes": await ListHeroesAsync(mineOnly: false); break;
            case "show": await ShowHeroAsync(Arg(parts, 1, "show <hero>")); break;
            case "breed": await BreedAsync(Arg(parts, 1, "breed <parentA> <parentB>"), Arg(parts, 2, "breed <parentA> <parentB>")); break;
            case "fight": await FightAsync(Arg(parts, 1, "fight <mine> <theirs>"), Arg(parts, 2, "fight <mine> <theirs>")); break;
            case "shop": await ShopAsync(); break;
            case "equip": await EquipAsync(Arg(parts, 1, "equip <hero> <itemId>"), Arg(parts, 2, "equip <hero> <itemId>")); break;
            case "info": await ChainInfoAsync(); break;
            case "quit" or "exit": return true;
            default:
                Console.WriteLine($"  unknown command '{parts[0]}' — try 'help'");
                break;
        }
        return false;
    }

    private static string Arg(string[] parts, int index, string usage)
        => parts.Length > index ? parts[index] : throw new GameClientException($"usage: {usage}");

    private static void PrintHelp() => Console.WriteLine("""
          register <name>        create a player (wallet + faucet balance)
          me                     profile + sats balance
          starter                claim your two generation-0 heroes
          mine                   list your heroes
          heroes                 list all heroes (find opponents)
          show <hero>            hero sheet (stats, skills, lineage, on-chain ids)
          breed <a> <b>          breed two of your heroes (commit-reveal, audited)
          fight <mine> <theirs>  battle another hero (commit-reveal, replay-audited)
          shop                   list equipment
          equip <hero> <itemId>  buy + equip an item
          info                   chain backend info
          quit                   exit
        heroes can be referenced by list number (1, 2, …) or id prefix.
        """);

    // ── HTTP helpers ───────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string path)
        => await ReadAsync<T>(await _http.GetAsync(path));

    private async Task<T> PostAsync<T>(string path, object? body = null)
        => await ReadAsync<T>(body is null
            ? await _http.PostAsync(path, null)
            : await _http.PostAsJsonAsync(path, body));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            throw new GameClientException(error?.Error ?? $"server returned {(int)response.StatusCode}");
        }
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private void RequireSession()
    {
        if (_me is null)
            throw new GameClientException("no session — 'register <name>' first");
    }

    // ── Hero references ────────────────────────────────────────────────

    private HeroDto ResolveHero(string reference)
    {
        if (int.TryParse(reference, out var index))
        {
            if (index < 1 || index > _lastListing.Count)
                throw new GameClientException($"no hero #{index} in the last listing — run 'mine' or 'heroes'");
            return _lastListing[index - 1];
        }
        var matches = _lastListing.Where(h => h.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new GameClientException($"no listed hero matches '{reference}' — run 'mine' or 'heroes' first"),
            _ => throw new GameClientException($"'{reference}' is ambiguous ({matches.Count} matches)"),
        };
    }

    private static string ShortId(string id) => id.Length <= 18 ? id : id[..18] + "…";

    private static string NewNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // ── Commands ───────────────────────────────────────────────────────

    private async Task RegisterAsync(string name)
    {
        var player = await PostAsync<PlayerDto>("/api/players", new RegisterPlayerRequest(name));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        _me = player;
        await SaveSessionAsync(player.Token!);
        Console.WriteLine($"  ✓ welcome, {player.Name}");
        Console.WriteLine($"    arkade address: {player.ArkadeAddress}");
        Console.WriteLine($"    balance: {player.BalanceSats} sats");
        Console.WriteLine("    next: 'starter' to mint your first two heroes");
    }

    private async Task ShowMeAsync()
    {
        RequireSession();
        _me = await GetAsync<PlayerDto>("/api/players/me");
        Console.WriteLine($"  {_me.Name}  ·  {_me.BalanceSats} sats  ·  {_me.ArkadeAddress}");
    }

    private async Task ClaimStarterAsync()
    {
        RequireSession();
        var starter = await PostAsync<StarterResponse>("/api/heroes/starter");
        Console.WriteLine("  ✓ two generation-0 heroes minted as Arkade assets:");
        foreach (var hero in starter.Heroes)
            Console.WriteLine($"    {hero.Name}  [{hero.Element}]  asset {ShortId(hero.AssetId ?? "?")}");
        Console.WriteLine("    'mine' to list them, 'show <n>' for details");
    }

    private async Task ListHeroesAsync(bool mineOnly)
    {
        var heroes = mineOnly
            ? await (Task<List<HeroDto>>)ListMineAsync()
            : await GetAsync<List<HeroDto>>("/api/heroes");
        _lastListing.Clear();
        _lastListing.AddRange(heroes);

        if (heroes.Count == 0)
        {
            Console.WriteLine(mineOnly ? "  no heroes yet — 'starter'" : "  no heroes exist yet");
            return;
        }
        for (var i = 0; i < heroes.Count; i++)
        {
            var h = heroes[i];
            var mine = _me is not null && h.OwnerId == _me.PlayerId ? "•" : " ";
            var cooldown = h.BreedCooldownUntil > DateTimeOffset.UtcNow ? " ⏳" : "";
            Console.WriteLine(
                $"  {mine}[{i + 1,2}] {h.Name,-22} gen{h.Generation}  lv{h.Level,2}  {h.Element,-8} " +
                $"hp{h.Stats.MaxHp,4} atk{h.Stats.Attack,3} def{h.Stats.Defense,3} spd{h.Stats.Speed,3}{cooldown}");
        }

        Task<List<HeroDto>> ListMineAsync()
        {
            RequireSession();
            return GetAsync<List<HeroDto>>("/api/heroes/mine");
        }
    }

    private async Task ShowHeroAsync(string reference)
    {
        var hero = await GetAsync<HeroDto>($"/api/heroes/{ResolveHero(reference).Id}");
        Console.WriteLine($"""
              {hero.Name}  (gen {hero.Generation}, level {hero.Level})
                element   {hero.Element}
                xp        {hero.Xp}/{hero.XpToNext}
                stats     hp {hero.Stats.MaxHp} · atk {hero.Stats.Attack} · mag {hero.Stats.Magic} · def {hero.Stats.Defense} · spd {hero.Stats.Speed} · luck {hero.Stats.Luck} · crit {hero.Stats.CritPercent}% · dodge {hero.Stats.DodgePercent}%
                skills    {string.Join(", ", hero.Skills.Select(s => $"{s.Name} (p{s.Power})"))}
                equipment {(hero.Equipment.Count == 0 ? "none" : string.Join(", ", hero.Equipment.Values))}
                breeding  count {hero.BreedCount}{(hero.BreedCooldownUntil > DateTimeOffset.UtcNow ? $", cooldown until {hero.BreedCooldownUntil:HH:mm:ss}" : "")}
                lineage   {(hero.ParentAId is null ? "genesis" : $"{ShortId(hero.ParentAId)} × {ShortId(hero.ParentBId!)}")}
                genome    {hero.GenomeHex}
                on-chain  asset {hero.AssetId ?? "-"}  tx {ShortId(hero.MintArkTxId ?? "-")}
            """);
    }

    private async Task BreedAsync(string refA, string refB)
    {
        RequireSession();
        var parentA = ResolveHero(refA);
        var parentB = ResolveHero(refB);

        var commit = await PostAsync<BreedCommitResponse>("/api/breeding/commit",
            new BreedCommitRequest(parentA.Id, parentB.Id));
        Console.WriteLine($"  committed: {ShortId(commit.CommitmentHex)} (fee {commit.FeeSats} sats)");

        var nonce = NewNonce();
        var reveal = await PostAsync<BreedRevealResponse>(
            $"/api/breeding/{commit.BreedingId}/reveal", new BreedRevealRequest(nonce));

        var child = reveal.Hero;
        Console.WriteLine($"  ✓ born: {child.Name}  gen{child.Generation}  [{child.Element}]");
        Console.WriteLine($"    hp{child.Stats.MaxHp} atk{child.Stats.Attack} mag{child.Stats.Magic} def{child.Stats.Defense} spd{child.Stats.Speed}");
        Console.WriteLine($"    asset {ShortId(child.AssetId ?? "?")}");

        var (ok, detail) = FairnessAudit.VerifyBreeding(parentA, parentB, nonce, commit.CommitmentHex, reveal);
        Console.WriteLine(ok
            ? $"    fairness ✓ {detail}"
            : $"    fairness ✗ SERVER CHEATED: {detail}");
    }

    private async Task FightAsync(string mineRef, string theirsRef)
    {
        RequireSession();
        var mine = ResolveHero(mineRef);
        var theirs = ResolveHero(theirsRef);

        var open = await PostAsync<OpenMatchResponse>("/api/matches/open",
            new OpenMatchRequest(mine.Id, theirs.Id));
        var nonce = NewNonce();
        var fight = await PostAsync<FightResponse>(
            $"/api/matches/{open.MatchId}/fight", new FightRequest(nonce));

        PrintBattle(fight);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, nonce, open.CommitmentHex, fight);
        Console.WriteLine(ok
            ? $"    fairness ✓ {detail}"
            : $"    fairness ✗ SERVER CHEATED: {detail}");
    }

    private void PrintBattle(FightResponse fight)
    {
        var names = new Dictionary<string, string>
        {
            [fight.ChallengerSnapshot.Id] = fight.ChallengerSnapshot.Name,
            [fight.DefenderSnapshot.Id] = fight.DefenderSnapshot.Name,
        };
        Console.WriteLine($"  ⚔ {fight.ChallengerSnapshot.Name} vs {fight.DefenderSnapshot.Name}");
        foreach (var e in fight.Result.Events)
        {
            var actor = names.GetValueOrDefault(e.ActorId, "?");
            var target = names.GetValueOrDefault(e.TargetId, "?");
            var line = e.Kind switch
            {
                "SkillUsed" =>
                    $"    t{e.Turn,2} {actor} → {e.SkillId} → {e.Damage} dmg{(e.Crit ? " CRIT" : "")}{(e.Healed > 0 ? $" (+{e.Healed} drained)" : "")}  [{target} {e.TargetHpAfter}hp]{(e.Note is null ? "" : $"  ({e.Note})")}",
                "Missed" => $"    t{e.Turn,2} {actor} → {e.SkillId} → missed",
                "Dodged" => $"    t{e.Turn,2} {actor} → {e.SkillId} → {target} dodged",
                "Defeated" => $"    t{e.Turn,2} ☠ {target} is defeated!",
                "TimeoutDecision" => $"    t{e.Turn,2} ⏱ {e.Note}",
                _ => $"    t{e.Turn,2} {e.Kind}",
            };
            Console.WriteLine(line);
        }
        var winnerName = names.GetValueOrDefault(fight.Result.WinnerId, fight.Result.WinnerId);
        Console.WriteLine($"  ✓ {winnerName} wins in {fight.Result.Turns} turns " +
                          $"({fight.Result.WinnerRemainingHp}/{fight.Result.WinnerMaxHp} hp left)");
        Console.WriteLine($"    xp: challenger +{fight.ChallengerXpAward}, defender +{fight.DefenderXpAward}" +
                          $"  (levels now {fight.ChallengerHero.Level}/{fight.DefenderHero.Level})");
    }

    private async Task ShopAsync()
    {
        var items = await GetAsync<List<ItemDto>>("/api/items");
        foreach (var group in items.GroupBy(i => i.Slot))
        {
            Console.WriteLine($"  {group.Key}:");
            foreach (var i in group)
            {
                var mods = new List<string>();
                if (i.MaxHp != 0) mods.Add($"hp{i.MaxHp:+#;-#}");
                if (i.Attack != 0) mods.Add($"atk{i.Attack:+#;-#}");
                if (i.Magic != 0) mods.Add($"mag{i.Magic:+#;-#}");
                if (i.Defense != 0) mods.Add($"def{i.Defense:+#;-#}");
                if (i.Speed != 0) mods.Add($"spd{i.Speed:+#;-#}");
                if (i.CritPercent != 0) mods.Add($"crit{i.CritPercent:+#;-#}%");
                Console.WriteLine($"    {i.Id,-16} {i.Name,-18} {string.Join(" ", mods),-28} {i.PriceSats,6} sats");
            }
        }
        Console.WriteLine("  'equip <hero> <itemId>' to buy");
    }

    private async Task EquipAsync(string heroRef, string itemId)
    {
        RequireSession();
        var hero = ResolveHero(heroRef);
        var result = await PostAsync<EquipResponse>($"/api/heroes/{hero.Id}/equip", new EquipRequest(itemId));
        Console.WriteLine($"  ✓ {result.Hero.Name} equipped {itemId} (paid, balance {result.BalanceSats} sats)");
        Console.WriteLine($"    stats now: hp{result.Hero.Stats.MaxHp} atk{result.Hero.Stats.Attack} mag{result.Hero.Stats.Magic} def{result.Hero.Stats.Defense} spd{result.Hero.Stats.Speed}");
    }

    private async Task ChainInfoAsync()
    {
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        Console.WriteLine($"  chain: {info.Mode} ({info.Network})");
        Console.WriteLine($"  treasury: {info.TreasuryAddress}");
        Console.WriteLine($"  species asset: {info.SpeciesAssetId ?? "-"}");
    }
}
