using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client;

public class GameClientException(string message) : Exception(message);

/// <summary>
/// Console game client. Deliberately minimal UI — the interesting part is that
/// every breed and fight is audited locally via <see cref="FairnessAudit"/>:
/// the client re-derives genomes and replays battles instead of trusting the
/// server's word.
/// </summary>
public class GameClient(string serverUrl) : IAsyncDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(serverUrl) };

    /// <summary>Per-player data dir (session + wallet). Override with ARKADE_HEROES_HOME to run several players side by side.</summary>
    private static readonly string HomeDir =
        Environment.GetEnvironmentVariable("ARKADE_HEROES_HOME") ?? AppContext.BaseDirectory;

    private static readonly string SessionFile = Path.Combine(HomeDir, "arkade-heroes-session.json");
    private static readonly string WalletDbFile = Path.Combine(HomeDir, "arkade-heroes-wallet.db");
    private static readonly string ReceiptsFile = Path.Combine(HomeDir, "arkade-heroes-receipts.json");

    // ── Progression receipts: player-held, server-signed facts ─────────

    private async Task<List<ProgressionReceiptDto>> LoadReceiptsAsync()
    {
        if (!File.Exists(ReceiptsFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ProgressionReceiptDto>>(
                await File.ReadAllTextAsync(ReceiptsFile)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task StoreReceiptAsync(ProgressionReceiptDto? receipt)
    {
        if (receipt is null) return;
        Directory.CreateDirectory(HomeDir);
        var receipts = await LoadReceiptsAsync();
        if (receipts.Any(r => r.Type == receipt.Type && r.Id == receipt.Id)) return;
        receipts.Add(receipt);
        await File.WriteAllTextAsync(ReceiptsFile, JsonSerializer.Serialize(receipts));
        var (ok, _) = ReceiptVerifier.Verify(receipt);
        Console.WriteLine(ok
            ? $"    receipt ✓ signed by the game — stored locally ({receipts.Count} held)"
            : "    receipt ✗ SIGNATURE INVALID — the server issued a bad receipt!");
    }

    private PlayerDto? _me;
    private string? _chainMode;
    private SelfCustodyWallet? _wallet;
    private readonly List<HeroDto> _lastListing = [];

    /// <summary>
    /// Opens (or creates) the player's self-custody wallet: keys generated and
    /// stored locally in <see cref="WalletDbFile"/>, never sent anywhere.
    /// TODO(tracked): encrypt the wallet db with a passphrase.
    /// </summary>
    private async Task<SelfCustodyWallet> WalletAsync()
    {
        if (_wallet is not null) return _wallet;
        Directory.CreateDirectory(HomeDir);
        Console.WriteLine("  opening self-custody wallet (keys stay on this machine)…");
        _wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_ARK") ?? "http://localhost:7070",
            DbPath = WalletDbFile,
        });
        Console.WriteLine($"    wallet address: {_wallet.Address}");
        return _wallet;
    }

    public async ValueTask DisposeAsync()
    {
        if (_wallet is not null) await _wallet.DisposeAsync();
        _http.Dispose();
    }

    private async Task<string> ChainModeAsync()
        => _chainMode ??= (await GetAsync<ChainInfoDto>("/api/chain/info")).Mode;

    /// <summary>
    /// Settles a fee invoice from the player's OWN wallet: the embedded
    /// self-custody wallet in NArk mode, or the dev simulation endpoint in
    /// InMemory mode. The server only ever observes the payment.
    /// </summary>
    private async Task<bool> SettleInvoiceAsync(FeeInvoiceDto invoice)
    {
        if (invoice.AmountSats == 0) return true;
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/pay-invoice", new { InvoiceId = invoice.InvoiceId });
            Console.WriteLine($"    paid {invoice.AmountSats} sats (simulated wallet) → {ShortId(invoice.PayToAddress)}");
            return true;
        }

        var wallet = await WalletAsync();
        try
        {
            var txid = await wallet.SendAsync(invoice.PayToAddress, invoice.AmountSats);
            Console.WriteLine($"    paid {invoice.AmountSats} sats from your wallet (tx {ShortId(txid)})");
            return true;
        }
        catch (Exception ex)
        {
            throw new GameClientException(
                $"could not pay the invoice from your wallet ({ex.Message}) — check 'wallet' and fund your address ('fund')");
        }
    }

    /// <summary>
    /// Retries an action that depends on the server OBSERVING our on-chain
    /// payment (reveal, claim, duel) — observation lags the send by a moment.
    /// </summary>
    private static async Task<T> RetryUntilObservedAsync<T>(Func<Task<T>> action, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        GameClientException? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await action();
            }
            catch (GameClientException ex) when (
                ex.Message.Contains("not been paid") || ex.Message.Contains("unpaid") ||
                ex.Message.Contains("does not show"))
            {
                last = ex;
                await Task.Delay(1500);
            }
        }
        throw new GameClientException($"{what}: the server did not observe the payment in time — {last?.Message}");
    }

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
            case "register": await RegisterAsync(Arg(parts, 1, "register <name> [arkadeAddress]"), parts.Length > 2 ? parts[2] : null); break;
            case "me": await ShowMeAsync(); break;
            case "starter": await ClaimStarterAsync(); break;
            case "mine": await ListHeroesAsync(mineOnly: true); break;
            case "heroes": await ListHeroesAsync(mineOnly: false); break;
            case "show": await ShowHeroAsync(Arg(parts, 1, "show <hero>")); break;
            case "breed": await BreedAsync(Arg(parts, 1, "breed <parentA> <parentB>"), Arg(parts, 2, "breed <parentA> <parentB>")); break;
            case "fight": await FightAsync(Arg(parts, 1, "fight <mine> <theirs>"), Arg(parts, 2, "fight <mine> <theirs>")); break;
            case "challenge": await ChallengeAsync(Arg(parts, 1, "challenge <mine> <theirs> <wagerSats> [covenant]"), Arg(parts, 2, "challenge <mine> <theirs> <wagerSats> [covenant]"), Arg(parts, 3, "challenge <mine> <theirs> <wagerSats> [covenant]"), parts.Length > 4 && parts[4].Equals("covenant", StringComparison.OrdinalIgnoreCase)); break;
            case "matches": await ListMatchesAsync(); break;
            case "accept": await AcceptAsync(Arg(parts, 1, "accept <matchId>")); break;
            case "duel": await DuelAsync(Arg(parts, 1, "duel <matchId>")); break;
            case "transfer": await TransferAsync(Arg(parts, 1, "transfer <hero> <playerId>"), Arg(parts, 2, "transfer <hero> <playerId>")); break;
            case "wallet": await WalletInfoAsync(); break;
            case "backup": await BackupAsync(); break;
            case "fund": await FundAsync(); break;
            case "receipts": await ListReceiptsAsync(); break;
            case "verify-receipts": await VerifyReceiptsAsync(); break;
            case "shop": await ShopAsync(); break;
            case "buy": await BuyAsync(Arg(parts, 1, "buy <itemId>")); break;
            case "claim": await ClaimAsync(Arg(parts, 1, "claim <invoiceId>")); break;
            case "equip": await EquipAsync(Arg(parts, 1, "equip <hero> <itemId>"), Arg(parts, 2, "equip <hero> <itemId>")); break;
            case "unequip": await UnequipAsync(Arg(parts, 1, "unequip <hero> <slot>"), Arg(parts, 2, "unequip <hero> <slot>")); break;
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
          fight <mine> <theirs>  friendly battle, no stakes (replay-audited)
          challenge <m> <t> <w> [covenant]  wagered match; 'covenant' = emulator-enforced escrow
          matches                list open/accepted wagered matches
          accept <matchId>       accept a wagered challenge against your hero
          duel <matchId>         resolve an accepted wagered match (challenger)
          transfer <hero> <pid>  send a hero (you sign; the Arkade asset moves wallets)
          wallet                 your self-custody wallet: address, balance, assets
          backup                 print your wallet mnemonic (guard it!)
          fund                   how to fund your wallet address
          receipts               your signed progression receipts (portable proof)
          verify-receipts        verify signatures + recompute levels from receipts
          shop                   list equipment
          buy <itemId>           buy an item (delivers a fungible Arkade asset unit)
          equip <hero> <itemId>  equip a held item unit
          unequip <hero> <slot>  free an item unit (Weapon/Armor/Trinket)
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

    private async Task RegisterAsync(string name, string? address)
    {
        // Non-custodial: registration binds YOUR address — in NArk mode it
        // comes from the embedded self-custody wallet automatically; in the
        // InMemory simulation a local sim-address stands in for it.
        if (address is null)
        {
            address = await ChainModeAsync() == "InMemory"
                ? $"sim-wallet-{NewNonce()}"
                : (await WalletAsync()).Address;
        }

        var player = await PostAsync<PlayerDto>("/api/players", new RegisterPlayerRequest(name, address));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        _me = player;
        await SaveSessionAsync(player.Token!);
        Console.WriteLine($"  ✓ welcome, {player.Name} — your keys, your heroes");
        Console.WriteLine($"    registered address: {player.ArkadeAddress}");
        Console.WriteLine($"    balance at address: {player.BalanceSats} sats");
        Console.WriteLine("    next: 'starter' to mint your first two heroes");
    }

    private async Task ShowMeAsync()
    {
        RequireSession();
        _me = await GetAsync<PlayerDto>("/api/players/me");
        Console.WriteLine($"  {_me.Name}  ·  {_me.BalanceSats} sats");
        Console.WriteLine($"  player id: {_me.PlayerId}   (others use this to transfer heroes to you)");
        Console.WriteLine($"  address:   {_me.ArkadeAddress}");
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
        Console.WriteLine($"  committed: {ShortId(commit.CommitmentHex)} (fee invoice {commit.Invoice.AmountSats} sats)");
        await SettleInvoiceAsync(commit.Invoice);

        var nonce = NewNonce();
        var reveal = await RetryUntilObservedAsync(
            () => PostAsync<BreedRevealResponse>(
                $"/api/breeding/{commit.BreedingId}/reveal", new BreedRevealRequest(nonce)),
            "breeding reveal");

        await StoreReceiptAsync(reveal.Receipt);

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
        await StoreReceiptAsync(fight.Receipt);

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

    private async Task ChallengeAsync(string mineRef, string theirsRef, string wagerText, bool covenant)
    {
        RequireSession();
        if (!long.TryParse(wagerText, out var wager) || wager <= 0)
            throw new GameClientException("wager must be a positive number of sats");
        var mine = ResolveHero(mineRef);
        var theirs = ResolveHero(theirsRef);

        var open = await PostAsync<OpenMatchResponse>("/api/matches/open",
            new OpenMatchRequest(mine.Id, theirs.Id, wager, covenant ? "covenant" : "invoice"));
        Console.WriteLine($"  ✓ challenge opened: {open.MatchId}{(covenant ? "  [covenant escrow]" : "")}");
        Console.WriteLine($"    wager {open.WagerSats} sats; commitment {ShortId(open.CommitmentHex)}");
        if (open.EscrowAddress is not null)
            await StakeEscrowAsync(open.MatchId, open.EscrowAddress, open.EscrowStakeSats);
        else if (open.StakeInvoice is not null)
            await SettleInvoiceAsync(open.StakeInvoice);
        Console.WriteLine($"    opponent runs 'accept {open.MatchId}', then you run 'duel {open.MatchId}'");
    }

    /// <summary>Stakes into a covenant escrow from the player's OWN wallet (or the dev simulator in InMemory mode).</summary>
    private async Task StakeEscrowAsync(string matchId, string escrowAddress, long stakeSats)
    {
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/stake-escrow", new { MatchId = matchId });
            Console.WriteLine($"    staked {stakeSats} sats into the escrow (simulated wallet)");
            return;
        }
        var wallet = await WalletAsync();
        var txid = await wallet.SendAsync(escrowAddress, stakeSats);
        Console.WriteLine($"    staked {stakeSats} sats into the covenant escrow from your wallet (tx {ShortId(txid)})");
    }

    private async Task DuelResolveAsync(string matchId)
    {
        var match = await GetAsync<MatchDto>($"/api/matches/{matchId}");
        var nonce = NewNonce();
        var fight = await RetryUntilObservedAsync(
            () => PostAsync<FightResponse>($"/api/matches/{matchId}/fight", new FightRequest(nonce)),
            "duel");

        PrintBattle(fight);
        if (fight.WinnerPayoutSats > 0)
            Console.WriteLine($"    💰 pot: {fight.WinnerPayoutSats} sats paid to the winner's owner");
        await StoreReceiptAsync(fight.Receipt);

        var (ok, detail) = FairnessAudit.VerifyMatch(matchId, nonce, match.CommitmentHex, fight);
        Console.WriteLine(ok
            ? $"    fairness ✓ {detail}"
            : $"    fairness ✗ SERVER CHEATED: {detail}");
    }

    private async Task ListMatchesAsync()
    {
        var open = await GetAsync<List<MatchDto>>("/api/matches?status=open");
        var accepted = await GetAsync<List<MatchDto>>("/api/matches?status=accepted");
        var interesting = open.Concat(accepted).Where(m => m.WagerSats > 0).ToList();
        if (interesting.Count == 0)
        {
            Console.WriteLine("  no open wagered matches");
            return;
        }
        foreach (var m in interesting)
            Console.WriteLine($"  {m.MatchId}  [{m.Status}]  wager {m.WagerSats} sats  " +
                              $"{ShortId(m.ChallengerHeroId)} vs {ShortId(m.DefenderHeroId)}");
    }

    private async Task AcceptAsync(string matchId)
    {
        RequireSession();
        var response = await PostAsync<AcceptMatchResponse>($"/api/matches/{matchId}/accept");
        if (response.EscrowAddress is not null)
        {
            Console.WriteLine($"  ✓ accepted — covenant escrow stake {response.EscrowStakeSats} sats");
            await StakeEscrowAsync(response.Match.MatchId, response.EscrowAddress, response.EscrowStakeSats);
        }
        else if (response.StakeInvoice is not null)
        {
            Console.WriteLine($"  ✓ accepted — stake invoice {response.StakeInvoice.AmountSats} sats");
            await SettleInvoiceAsync(response.StakeInvoice);
        }
        Console.WriteLine($"    challenger resolves with 'duel {response.Match.MatchId}'");
    }

    private async Task DuelAsync(string matchId)
    {
        RequireSession();
        await DuelResolveAsync(matchId);
    }

    private async Task TransferAsync(string heroRef, string toPlayerId)
    {
        RequireSession();
        var hero = ResolveHero(heroRef);

        // Non-custodial: the asset spend is OURS to make; the server only verifies.
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/transfer-asset",
                new { AssetId = hero.AssetId ?? hero.Id, ToPlayerId = toPlayerId });
            Console.WriteLine($"    asset moved (simulated wallet)");
        }
        else
        {
            var recipient = await GetAsync<PlayerDto>($"/api/players/{toPlayerId}");
            var wallet = await WalletAsync();
            var txid = await wallet.SendAssetAsync(recipient.ArkadeAddress, hero.AssetId ?? hero.Id, 1);
            Console.WriteLine($"    hero asset sent from your wallet to {recipient.Name}'s address (tx {ShortId(txid)})");
        }

        var result = await RetryUntilObservedAsync(
            () => PostAsync<TransferResponse>($"/api/heroes/{hero.Id}/transfer",
                new TransferRequest(toPlayerId)),
            "transfer confirmation");
        Console.WriteLine($"  ✓ {result.Hero.Name} transferred to {toPlayerId} (verified on-chain)");
    }

    private async Task WalletInfoAsync()
    {
        if (await ChainModeAsync() == "InMemory")
        {
            Console.WriteLine("  InMemory mode — the simulated wallet lives server-side (dev endpoints)");
            return;
        }
        var wallet = await WalletAsync();
        var balance = await wallet.GetBalanceSatsAsync();
        var assets = await wallet.GetAssetsAsync();
        Console.WriteLine($"  address  {wallet.Address}");
        Console.WriteLine($"  balance  {balance} sats");
        Console.WriteLine($"  assets   {(assets.Count == 0 ? "none" : "")}");
        foreach (var (assetId, amount) in assets)
            Console.WriteLine($"    {ShortId(assetId)} × {amount}");
    }

    private async Task BackupAsync()
    {
        if (await ChainModeAsync() == "InMemory")
            throw new GameClientException("InMemory mode has no real wallet to back up");
        var wallet = await WalletAsync();
        Console.WriteLine("  ⚠ your mnemonic — anyone with these words controls your heroes and funds:");
        Console.WriteLine($"    {wallet.Mnemonic}");
    }

    private async Task ListReceiptsAsync()
    {
        var receipts = await LoadReceiptsAsync();
        if (receipts.Count == 0)
        {
            Console.WriteLine("  no receipts yet — they arrive with every breed and fight");
            return;
        }
        foreach (var r in receipts.OrderBy(r => r.UnixSeconds))
            Console.WriteLine($"  {r.Type,-9} {ShortId(r.Id)}  {ShortId(r.HeroAId)} vs {ShortId(r.HeroBId)}  " +
                              $"xp {r.XpAwardA}/{r.XpAwardB}  {DateTimeOffset.FromUnixTimeSeconds(r.UnixSeconds):HH:mm:ss}");
        Console.WriteLine($"  {receipts.Count} receipt(s) held — your progression, portable and provable");
    }

    private async Task VerifyReceiptsAsync()
    {
        RequireSession();
        var chainInfo = await GetAsync<ChainInfoDto>("/api/chain/info");
        var held = await LoadReceiptsAsync();

        // Signature + commit-reveal verification on everything we hold.
        var bad = 0;
        foreach (var receipt in held)
        {
            var (ok, detail) = ReceiptVerifier.Verify(receipt);
            var keyMatches = chainInfo.GameSignerKey is null ||
                             string.Equals(receipt.GameSignerKeyHex, chainInfo.GameSignerKey, StringComparison.OrdinalIgnoreCase);
            if (!ok || !keyMatches)
            {
                bad++;
                Console.WriteLine($"  ✗ {receipt.Type} {ShortId(receipt.Id)}: {(ok ? "signed by an unknown key" : detail)}");
            }
        }
        Console.WriteLine(bad == 0
            ? $"  ✓ all {held.Count} receipt(s) verify against the game key {ShortId(chainInfo.GameSignerKey ?? "?")}"
            : $"  {bad}/{held.Count} receipts FAILED verification");

        // Level replay: pull each hero's full public receipt chain and recompute.
        var mine = await GetAsync<List<HeroDto>>("/api/heroes/mine");
        foreach (var hero in mine)
        {
            var chain = await GetAsync<List<ProgressionReceiptDto>>($"/api/receipts/hero/{hero.Id}");
            var expected = ReceiptVerifier.ReplayLevel(hero.Id, chain);
            var match = expected == hero.Level ? "✓" : "✗";
            Console.WriteLine($"  {match} {hero.Name}: level {hero.Level} (recomputed {expected} from {chain.Count} receipt(s))");
        }
    }

    private async Task FundAsync()
    {
        if (await ChainModeAsync() == "InMemory")
        {
            Console.WriteLine("  InMemory mode — every player starts with a simulated balance");
            return;
        }
        var wallet = await WalletAsync();
        Console.WriteLine($"  send sats to your address: {wallet.Address}");
        Console.WriteLine($"  regtest faucet: node regtest/regtest.mjs ark send --to {wallet.Address} --amount 100000 --password secret");
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
        Console.WriteLine("  'buy <itemId>' to purchase, then 'equip <hero> <itemId>'");
    }

    private async Task BuyAsync(string itemId)
    {
        RequireSession();
        var invoice = (await PostAsync<ItemInvoiceResponse>($"/api/items/{itemId}/buy")).Invoice;
        Console.WriteLine($"  invoice: {invoice.AmountSats} sats for {itemId}");
        await SettleInvoiceAsync(invoice);
        var claim = await RetryUntilObservedAsync(
            () => PostAsync<ClaimItemResponse>("/api/items/claim", new ClaimItemRequest(invoice.InvoiceId)),
            "item claim");
        Console.WriteLine($"  ✓ bought {itemId} — you now hold {claim.UnitsHeld} unit(s)");
        Console.WriteLine($"    item asset {ShortId(claim.ItemAssetId)}  tx {ShortId(claim.ArkTxId)}");
    }

    private async Task ClaimAsync(string invoiceId)
    {
        RequireSession();
        var claim = await PostAsync<ClaimItemResponse>("/api/items/claim", new ClaimItemRequest(invoiceId));
        Console.WriteLine($"  ✓ claimed — you now hold {claim.UnitsHeld} unit(s) (asset {ShortId(claim.ItemAssetId)})");
    }

    private async Task EquipAsync(string heroRef, string itemId)
    {
        RequireSession();
        var hero = ResolveHero(heroRef);
        var result = await PostAsync<EquipResponse>($"/api/heroes/{hero.Id}/equip", new EquipRequest(itemId));
        Console.WriteLine($"  ✓ {result.Hero.Name} equipped {itemId}");
        Console.WriteLine($"    stats now: hp{result.Hero.Stats.MaxHp} atk{result.Hero.Stats.Attack} mag{result.Hero.Stats.Magic} def{result.Hero.Stats.Defense} spd{result.Hero.Stats.Speed}");
    }

    private async Task UnequipAsync(string heroRef, string slot)
    {
        RequireSession();
        var hero = ResolveHero(heroRef);
        var result = await PostAsync<EquipResponse>($"/api/heroes/{hero.Id}/unequip", new UnequipRequest(slot));
        Console.WriteLine($"  ✓ {result.Hero.Name} unequipped {slot} — the item unit is free for another hero");
    }

    private async Task ChainInfoAsync()
    {
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        Console.WriteLine($"  chain: {info.Mode} ({info.Network})");
        Console.WriteLine($"  treasury: {info.TreasuryAddress}");
        Console.WriteLine($"  species asset: {info.SpeciesAssetId ?? "-"}");
        Console.WriteLine($"  covenant co-signer (emulator): {info.EmulatorSignerKey ?? "not reachable"}");
    }
}
