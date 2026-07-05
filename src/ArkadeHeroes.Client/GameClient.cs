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
public class GameClient : IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Per-player data dir (session + wallet + receipts). Override with ARKADE_HEROES_HOME to run several players side by side.</summary>
    private readonly string HomeDir;
    private readonly string SessionFile;
    private readonly string WalletDbFile;
    private readonly string ReceiptsFile;

    /// <summary>
    /// <paramref name="httpClient"/> and <paramref name="homeDir"/> are injectable
    /// for tests (drive the client against an in-memory server with an isolated
    /// data dir); production passes neither, keeping today's behaviour — an own
    /// <see cref="HttpClient"/> for <paramref name="serverUrl"/> and the
    /// <c>ARKADE_HEROES_HOME</c> data dir.
    /// </summary>
    public GameClient(string serverUrl, HttpClient? httpClient = null, string? homeDir = null)
    {
        _serverUrl = serverUrl;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(serverUrl) };
        _ownsHttp = httpClient is null;
        HomeDir = homeDir ?? Environment.GetEnvironmentVariable("ARKADE_HEROES_HOME") ?? AppContext.BaseDirectory;
        SessionFile = Path.Combine(HomeDir, "arkade-heroes-session.json");
        WalletDbFile = Path.Combine(HomeDir, "arkade-heroes-wallet.db");
        ReceiptsFile = Path.Combine(HomeDir, "arkade-heroes-receipts.json");
    }

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
    /// stored locally in <see cref="WalletDbFile"/>, never sent anywhere. Set
    /// <c>ARKADE_HEROES_WALLET_PASSPHRASE</c> to encrypt the mnemonic at rest
    /// (AES-256-GCM); the SAME passphrase is then required to reopen the wallet.
    /// </summary>
    private async Task<SelfCustodyWallet> WalletAsync()
    {
        if (_wallet is not null) return _wallet;
        Directory.CreateDirectory(HomeDir);
        var passphrase = Environment.GetEnvironmentVariable("ARKADE_HEROES_WALLET_PASSPHRASE");
        var encrypted = !string.IsNullOrEmpty(passphrase);
        Console.WriteLine($"  opening self-custody wallet (keys stay on this machine{(encrypted ? ", encrypted at rest" : "")})…");
        try
        {
            _wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
            {
                ArkUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_ARK") ?? "http://localhost:7070",
                DbPath = WalletDbFile,
                Passphrase = passphrase,
            });
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException
                                   && ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
        {
            throw new GameClientException(
                "could not open your wallet — set ARKADE_HEROES_WALLET_PASSPHRASE to the passphrase it was encrypted with");
        }
        Console.WriteLine($"    wallet address: {_wallet.Address}");
        return _wallet;
    }

    public async ValueTask DisposeAsync()
    {
        if (_wallet is not null) await _wallet.DisposeAsync();
        if (_ownsHttp) _http.Dispose(); // an injected client is owned by the caller
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
            if (session?.Token is null || session.ServerUrl != _serverUrl) return;
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
    {
        // A custom ARKADE_HEROES_HOME may not exist yet, and `register` saves the
        // session before any wallet op that would create it — ensure it first.
        Directory.CreateDirectory(HomeDir);
        await File.WriteAllTextAsync(SessionFile,
            JsonSerializer.Serialize(new SessionState(_serverUrl, token)));
    }

    private record SessionState(string ServerUrl, string Token);

    // ── Command dispatch ───────────────────────────────────────────────

    public async Task<bool> ExecuteAsync(string[] parts)
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "help": PrintHelp(); break;
            case "register": await RegisterAsync(Arg(parts, 1, "register <name> [arkadeAddress]"), parts.Length > 2 ? parts[2] : null); break;
            case "login": await LoginAsync(); break;
            case "me": await ShowMeAsync(); break;
            case "starter": await ClaimStarterAsync(); break;
            case "mine": await ListHeroesAsync(mineOnly: true); break;
            case "heroes": await ListHeroesAsync(mineOnly: false); break;
            case "show": await ShowHeroAsync(Arg(parts, 1, "show <hero>")); break;
            case "breed": await BreedAsync(Arg(parts, 1, "breed <parentA> <parentB> [covenant]"), Arg(parts, 2, "breed <parentA> <parentB> [covenant]"), parts.Length > 3 && parts[3].Equals("covenant", StringComparison.OrdinalIgnoreCase)); break;
            case "fight": await FightAsync(Arg(parts, 1, "fight <mine> <theirs>"), Arg(parts, 2, "fight <mine> <theirs>")); break;
            case "challenge": await ChallengeAsync(Arg(parts, 1, "challenge <mine> <theirs> <wagerSats> [covenant]"), Arg(parts, 2, "challenge <mine> <theirs> <wagerSats> [covenant]"), Arg(parts, 3, "challenge <mine> <theirs> <wagerSats> [covenant]"), parts.Length > 4 && parts[4].Equals("covenant", StringComparison.OrdinalIgnoreCase)); break;
            case "matches": await ListMatchesAsync(); break;
            case "accept": await AcceptAsync(Arg(parts, 1, "accept <matchId>")); break;
            case "duel": await DuelAsync(Arg(parts, 1, "duel <matchId>")); break;
            case "refund": await RefundAsync(Arg(parts, 1, "refund <matchId>")); break;
            case "transfer": await TransferAsync(Arg(parts, 1, "transfer <hero> <playerId>"), Arg(parts, 2, "transfer <hero> <playerId>")); break;
            case "wallet": await WalletInfoAsync(); break;
            case "backup": await BackupAsync(); break;
            case "restore": await RestoreWalletAsync(string.Join(' ', parts.Skip(1))); break;
            case "fund": await FundAsync(); break;
            case "top": await LeaderboardAsync(); break;
            case "receipts": await ListReceiptsAsync(); break;
            case "verify-receipts": await VerifyReceiptsAsync(); break;
            case "shop": await ShopAsync(); break;
            case "buy": await BuyAsync(Arg(parts, 1, "buy <itemId>")); break;
            case "claim": await ClaimAsync(Arg(parts, 1, "claim <invoiceId>")); break;
            case "sell": await SellAsync(Arg(parts, 1, "sell <itemId> <askSats>"), Arg(parts, 2, "sell <itemId> <askSats>")); break;
            case "sellhero": await SellHeroAsync(Arg(parts, 1, "sellhero <hero> <askSats>"), Arg(parts, 2, "sellhero <hero> <askSats>")); break;
            case "offers": await ListOffersAsync(); break;
            case "buyoffer": await BuyOfferAsync(Arg(parts, 1, "buyoffer <offerId>")); break;
            case "buyhero": await BuyHeroAsync(Arg(parts, 1, "buyhero <offerId>")); break;
            case "canceloffer": await CancelOfferAsync(Arg(parts, 1, "canceloffer <offerId>")); break;
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
          login                  resume an existing player by signing with your wallet
          me                     profile + sats balance
          starter                claim your two generation-0 heroes
          mine                   list your heroes
          heroes                 list all heroes (find opponents)
          show <hero>            hero sheet (stats, skills, lineage, on-chain ids)
          breed <a> <b> [covenant]  breed two heroes; 'covenant' = emulator-enforced escrow mint
          fight <mine> <theirs>  friendly battle, no stakes (replay-audited)
          challenge <m> <t> <w> [covenant]  wagered match; 'covenant' = emulator-enforced escrow
          matches                list open/accepted wagered matches
          accept <matchId>       accept a wagered challenge against your hero
          duel <matchId>         resolve an accepted wagered match (challenger)
          refund <matchId>       reclaim your covenant stake after expiry (no server trust)
          transfer <hero> <pid>  send a hero (you sign; the Arkade asset moves wallets)
          wallet                 your self-custody wallet: address, balance, assets
          backup                 print your wallet mnemonic (guard it!)
          restore <12 words>     recover your wallet (heroes + funds) from your mnemonic
          fund                   how to fund your wallet address
          top                    leaderboard (wins/level, recomputed from receipts)
          receipts               your signed progression receipts (portable proof)
          verify-receipts        verify signatures + recompute levels from receipts
          shop                   list equipment
          buy <itemId>           buy an item (delivers a fungible Arkade asset unit)
          equip <hero> <itemId>  equip a held item unit
          unequip <hero> <slot>  free an item unit (Weapon/Armor/Trinket)
          sell <itemId> <ask>    list a spare item for sale (covenant-enforced offer)
          sellhero <hero> <ask>  list one of your heroes for sale (same covenant)
          offers                 browse resting offers (items and heroes)
          buyoffer <offerId>     buy an item offer; you pay the seller directly (covenant-enforced)
          buyhero <offerId>      buy a hero offer, then claim ownership (covenant-enforced)
          canceloffer <offerId>  reclaim your unsold offer after expiry (no server trust)
          info                   chain backend info
          quit                   exit
        heroes can be referenced by list number (1, 2, …) or id prefix.
        """);

    /// <summary>
    /// Reclaims this player's stake from an abandoned covenant match. In NArk
    /// mode the contracts are rebuilt LOCALLY from the match's public escrow
    /// params and the refund is spent by the player's own wallet through the
    /// emulator — the server is only consulted for the (verifiable) params.
    /// </summary>
    private async Task RefundAsync(string matchId)
    {
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/refund-escrow", new { MatchId = matchId });
            Console.WriteLine("    stake refunded (simulated wallet)");
            return;
        }

        var escrow = await GetAsync<Chain.Covenants.WagerEscrowParams>($"/api/matches/{matchId}/escrow");
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        var emulatorUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_EMULATOR") ?? info.EmulatorUri
            ?? throw new GameClientException("the server did not advertise an emulator URI — set ARKADE_HEROES_EMULATOR");
        var esploraApi = Environment.GetEnvironmentVariable("ARKADE_HEROES_ESPLORA") ?? info.EsploraApiUri
            ?? throw new GameClientException("no esplora API for chain time — set ARKADE_HEROES_ESPLORA (e.g. http://localhost:8999/api/v1)");

        var wallet = await WalletAsync();
        var balanceBefore = await wallet.GetBalanceSatsAsync();
        Console.WriteLine($"    rebuilding escrow contracts locally (stake {escrow.StakeSats} sats, refundable after {escrow.RefundAfterUnixSeconds})…");
        try
        {
            await Chain.Covenants.EscrowRefundFlow.RefundAsync(
                wallet, new Uri(emulatorUri), escrow,
                ct => Chain.Covenants.EsploraChainTime.GetMedianTimeAsync(_http, esploraApi, ct));
        }
        catch (Chain.Covenants.RefundNotYetDueException ex)
        {
            Console.WriteLine($"    not yet: refund unlocks at chain time {ex.DueUnixSeconds}, chain is at {ex.ChainUnixSeconds} — try again later");
            return;
        }
        Console.WriteLine("    refund co-signed — waiting for the stake to land in your wallet…");
        await wallet.WaitForBalanceAsync(balanceBefore + escrow.StakeSats, TimeSpan.FromSeconds(90));
        Console.WriteLine($"    reclaimed {escrow.StakeSats} sats — balance {balanceBefore + escrow.StakeSats}+");
    }

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
        // InMemory simulation a local sim-address stands in for it. In NArk mode
        // we also register the wallet's login pubkey so a restored wallet can
        // later resume this player via 'login' (sign in with your wallet).
        string? loginPubKey = null, loginNonce = null, loginSig = null;
        if (address is null)
        {
            if (await ChainModeAsync() == "InMemory")
            {
                address = $"sim-wallet-{NewNonce()}";
            }
            else
            {
                var wallet = await WalletAsync();
                address = wallet.Address;
                loginPubKey = wallet.LoginPubKeyHex;
                // Proof-of-possession: sign a fresh challenge so this login key
                // can only be registered by whoever actually controls it.
                var challenge = await GetAsync<LoginChallengeResponse>("/api/players/login-challenge");
                (_, loginSig) = wallet.SignLoginDigest(LoginChallenge.Digest(challenge.NonceHex));
                loginNonce = challenge.NonceHex;
            }
        }

        var player = await PostAsync<PlayerDto>("/api/players",
            new RegisterPlayerRequest(name, address, loginPubKey, loginNonce, loginSig));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        _me = player;
        await SaveSessionAsync(player.Token!);
        Console.WriteLine($"  ✓ welcome, {player.Name} — your keys, your heroes");
        Console.WriteLine($"    registered address: {player.ArkadeAddress}");
        Console.WriteLine($"    balance at address: {player.BalanceSats} sats");
        Console.WriteLine("    next: 'starter' to mint your first two heroes");
    }

    /// <summary>
    /// "Sign in with your wallet": resume an existing player by signing a
    /// server challenge with the wallet's login key. After a 'restore' on a new
    /// machine this re-attaches you to your heroes — non-custodial auth, no
    /// password, the server never holds a key.
    /// </summary>
    private async Task LoginAsync()
    {
        if (await ChainModeAsync() == "InMemory")
            throw new GameClientException("InMemory mode has no wallet to sign in with");
        var wallet = await WalletAsync();
        var challenge = await GetAsync<LoginChallengeResponse>("/api/players/login-challenge");
        var (pubKey, signature) = wallet.SignLoginDigest(LoginChallenge.Digest(challenge.NonceHex));
        var player = await PostAsync<PlayerDto>("/api/players/login",
            new LoginRequest(pubKey, challenge.NonceHex, signature));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        _me = player;
        await SaveSessionAsync(player.Token!);
        Console.WriteLine($"  ✓ signed in as {player.Name} — session resumed with your wallet");
        Console.WriteLine($"    address {player.ArkadeAddress}   balance {player.BalanceSats} sats");
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

    private async Task BreedAsync(string refA, string refB, bool covenant)
    {
        RequireSession();
        var parentA = ResolveHero(refA);
        var parentB = ResolveHero(refB);

        var commit = await PostAsync<BreedCommitResponse>("/api/breeding/commit",
            new BreedCommitRequest(parentA.Id, parentB.Id, covenant ? "covenant" : "invoice"));

        if (covenant)
        {
            Console.WriteLine($"  committed: {ShortId(commit.CommitmentHex)}  [covenant breed escrow]");
            await DepositBreedEscrowAsync(commit.BreedingId, commit.EscrowAddress!, commit.EscrowFeeSats, parentA, parentB);
        }
        else
        {
            Console.WriteLine($"  committed: {ShortId(commit.CommitmentHex)} (fee invoice {commit.Invoice!.AmountSats} sats)");
            await SettleInvoiceAsync(commit.Invoice);
        }

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

    /// <summary>Deposits BOTH parents + the fee into a breed escrow from the player's OWN wallet (or the dev simulator in InMemory mode).</summary>
    private async Task DepositBreedEscrowAsync(string breedingId, string escrowAddress, long feeSats, HeroDto parentA, HeroDto parentB)
    {
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/fund-breed-escrow", new { BreedingId = breedingId });
            Console.WriteLine("    deposited both parents + fee into the breed escrow (simulated wallet)");
            return;
        }
        var wallet = await WalletAsync();
        await wallet.SendAssetAsync(escrowAddress, parentA.AssetId ?? parentA.Id, 1);
        await wallet.SendAssetAsync(escrowAddress, parentB.AssetId ?? parentB.Id, 1);
        await wallet.SendAsync(escrowAddress, feeSats);
        Console.WriteLine($"    deposited both parents + {feeSats}-sat fee into the breed escrow from your wallet");
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
        Console.WriteLine("    write it down; 'restore <the 12 words>' brings your wallet back on any machine");
    }

    /// <summary>
    /// Recovers a wallet from its mnemonic into a FRESH data dir — the
    /// non-custodial guarantee made concrete: your heroes (on-chain assets) and
    /// funds come back from your words ALONE, with no server or custodian. The
    /// same 12 words always re-derive the same address, so the on-chain assets
    /// sitting there are yours again.
    /// </summary>
    private async Task RestoreWalletAsync(string mnemonic)
    {
        if (await ChainModeAsync() == "InMemory")
            throw new GameClientException("InMemory mode has no real wallet to restore");
        if (_wallet is not null)
            throw new GameClientException("a wallet is already open — restore into a fresh ARKADE_HEROES_HOME");
        if (File.Exists(WalletDbFile))
            throw new GameClientException($"a wallet already exists in {HomeDir} — restore into a fresh ARKADE_HEROES_HOME so it isn't overwritten");
        mnemonic = mnemonic.Trim();
        if (mnemonic.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is not (12 or 24))
            throw new GameClientException("a mnemonic is 12 (or 24) words — paste all of them after 'restore'");

        Directory.CreateDirectory(HomeDir);
        Console.WriteLine("  restoring your wallet from the mnemonic (keys stay on this machine)…");
        try
        {
            _wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
            {
                ArkUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_ARK") ?? "http://localhost:7070",
                DbPath = WalletDbFile,
                Mnemonic = mnemonic,
                Passphrase = Environment.GetEnvironmentVariable("ARKADE_HEROES_WALLET_PASSPHRASE"),
            });
        }
        catch (Exception ex)
        {
            throw new GameClientException($"could not restore — is the mnemonic correct? ({ex.Message})");
        }

        Console.WriteLine($"  ✓ wallet restored — address {_wallet.Address}");
        var balance = await _wallet.GetBalanceSatsAsync();
        var assets = await _wallet.GetAssetsAsync();
        Console.WriteLine($"    balance {balance} sats; {assets.Count} on-chain asset(s) recovered from your words alone");
        foreach (var (assetId, amount) in assets)
            Console.WriteLine($"    {ShortId(assetId)} × {amount}");
        Console.WriteLine("    your heroes/items live in this wallet; 'register <name>' to (re)join a server with it");
    }

    private async Task LeaderboardAsync()
    {
        var board = await GetAsync<List<LeaderboardEntryDto>>("/api/leaderboard");
        if (board.Count == 0)
        {
            Console.WriteLine("  no heroes yet");
            return;
        }
        Console.WriteLine("  #   hero            lvl  wins  matches");
        foreach (var e in board.Take(20))
            Console.WriteLine($"  {e.Rank,-3} {e.Name,-15} {e.Level,3}  {e.Wins,4}  {e.Matches,7}");
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

    // ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ──

    /// <summary>Lists a spare item unit for sale, then deposits it into the offer address from the player's own wallet.</summary>
    private async Task SellAsync(string itemId, string askText)
    {
        RequireSession();
        if (!long.TryParse(askText, out var ask) || ask <= 0)
            throw new GameClientException("ask must be a positive number of sats");
        var offer = await PostAsync<CreateOfferResponse>("/api/offers", new CreateOfferRequest(itemId, ask));
        Console.WriteLine($"  ✓ offer {ShortId(offer.OfferId)} created — ask {offer.AskSats} sats for one {itemId}");
        await DepositOfferAsync(offer.OfferId, offer.OfferAddress, offer.ItemAssetId);
        Console.WriteLine($"    listed — buyers run 'offers' then 'buyoffer {offer.OfferId}'");
    }

    /// <summary>Deposits one item unit into the offer address from the player's OWN wallet (or the dev simulator in InMemory mode).</summary>
    private async Task DepositOfferAsync(string offerId, string offerAddress, string itemAssetId)
    {
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/fund-offer", new { OfferId = offerId });
            Console.WriteLine("    deposited the item unit into the offer (simulated wallet)");
            return;
        }
        var wallet = await WalletAsync();
        var txid = await wallet.SendAssetAsync(offerAddress, itemAssetId, 1);
        Console.WriteLine($"    deposited one unit into the offer address from your wallet (tx {ShortId(txid)})");
    }

    /// <summary>Lists one of your HEROES for sale (unique asset), then deposits it into the offer address.</summary>
    private async Task SellHeroAsync(string heroRef, string askText)
    {
        RequireSession();
        if (!long.TryParse(askText, out var ask) || ask <= 0)
            throw new GameClientException("ask must be a positive number of sats");
        var hero = ResolveHero(heroRef);
        var offer = await PostAsync<CreateOfferResponse>("/api/offers/hero", new CreateHeroOfferRequest(hero.Id, ask));
        Console.WriteLine($"  ✓ offer {ShortId(offer.OfferId)} created — ask {offer.AskSats} sats for {hero.Name}");
        await DepositOfferAsync(offer.OfferId, offer.OfferAddress, offer.ItemAssetId);
        Console.WriteLine($"    {hero.Name} listed — buyers run 'offers' then 'buyhero {offer.OfferId}'");
    }

    /// <summary>
    /// Buys a resting HERO offer: fulfils the covenant from the buyer's own wallet
    /// (same trustless rebuild as an item offer), then claims game-side ownership
    /// — the server verifies the chain shows the buyer holding the hero asset and
    /// reassigns the record (equipment stays with the seller).
    /// </summary>
    private async Task BuyHeroAsync(string offerId)
    {
        RequireSession();
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/fulfill-offer", new { OfferId = offerId });
            var simClaim = await PostAsync<TransferResponse>($"/api/offers/{offerId}/claim-hero");
            Console.WriteLine($"  ✓ bought {simClaim.Hero.Name} — hero delivered, seller paid (simulated wallet)");
            return;
        }

        var offer = await GetAsync<Chain.Covenants.OfferParams>($"/api/offers/{offerId}/params");
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        var emulatorUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_EMULATOR") ?? info.EmulatorUri
            ?? throw new GameClientException("the server did not advertise an emulator URI — set ARKADE_HEROES_EMULATOR");

        var wallet = await WalletAsync();
        Console.WriteLine($"    rebuilding the offer covenant locally (ask {offer.AskSats} sats to {ShortId(offer.SellerAddress)})…");
        await Chain.Covenants.OfferFulfillFlow.FulfillAsync(wallet, new Uri(emulatorUri), offer);
        Console.WriteLine("    fulfilment co-signed — waiting for the hero to land in your wallet…");
        await wallet.WaitForAssetAsync(offer.ItemAssetId, TimeSpan.FromSeconds(90));
        var claimed = await PostAsync<TransferResponse>($"/api/offers/{offerId}/claim-hero");
        Console.WriteLine($"  ✓ bought {claimed.Hero.Name} — you paid {offer.AskSats} sats and now own the hero");
    }

    private async Task ListOffersAsync()
    {
        var offers = await GetAsync<List<OfferDto>>("/api/offers");
        if (offers.Count == 0)
        {
            Console.WriteLine("  no offers resting — list one with 'sell <itemId> <askSats>'");
            return;
        }
        Console.WriteLine("  offer              kind  name              ask       seller");
        foreach (var o in offers)
            Console.WriteLine($"  {ShortId(o.OfferId),-18} {o.Kind,-5} {o.ItemName,-16} {o.AskSats,6} sats  {ShortId(o.SellerId)}");
        Console.WriteLine("  buy with 'buyoffer <id>' (item) or 'buyhero <id>' (hero) — you pay the seller directly; the covenant enforces the ask");
    }

    /// <summary>
    /// Buys a resting offer. In NArk mode the buyer rebuilds the offer covenant
    /// LOCALLY from its public params, funds the ask from their OWN wallet, and
    /// takes the item through the emulator — the server is only consulted for the
    /// (verifiable) params, and the covenant refuses any underpayment.
    /// </summary>
    private async Task BuyOfferAsync(string offerId)
    {
        RequireSession();
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/fulfill-offer", new { OfferId = offerId });
            Console.WriteLine($"  ✓ bought offer {ShortId(offerId)} — item delivered, seller paid (simulated wallet)");
            return;
        }

        var offer = await GetAsync<Chain.Covenants.OfferParams>($"/api/offers/{offerId}/params");
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        var emulatorUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_EMULATOR") ?? info.EmulatorUri
            ?? throw new GameClientException("the server did not advertise an emulator URI — set ARKADE_HEROES_EMULATOR");

        var wallet = await WalletAsync();
        Console.WriteLine($"    rebuilding the offer covenant locally (ask {offer.AskSats} sats to {ShortId(offer.SellerAddress)})…");
        await Chain.Covenants.OfferFulfillFlow.FulfillAsync(wallet, new Uri(emulatorUri), offer);
        Console.WriteLine("    fulfilment co-signed — waiting for the item to land in your wallet…");
        await wallet.WaitForAssetAsync(offer.ItemAssetId, TimeSpan.FromSeconds(90));
        Console.WriteLine($"  ✓ bought offer {ShortId(offerId)} — you paid {offer.AskSats} sats and now hold the item");
    }

    /// <summary>
    /// Cancels an unsold offer, reclaiming the item after the covenant's expiry.
    /// In NArk mode the contract is rebuilt LOCALLY and the reclaim is spent by
    /// the seller's own wallet through the emulator (gated on the chain clock).
    /// </summary>
    private async Task CancelOfferAsync(string offerId)
    {
        RequireSession();
        if (await ChainModeAsync() == "InMemory")
        {
            await PostAsync<object>("/api/dev/reclaim-offer", new { OfferId = offerId });
            Console.WriteLine($"  ✓ offer {ShortId(offerId)} cancelled — item returned (simulated wallet)");
            return;
        }

        var offer = await GetAsync<Chain.Covenants.OfferParams>($"/api/offers/{offerId}/params");
        var info = await GetAsync<ChainInfoDto>("/api/chain/info");
        var emulatorUri = Environment.GetEnvironmentVariable("ARKADE_HEROES_EMULATOR") ?? info.EmulatorUri
            ?? throw new GameClientException("the server did not advertise an emulator URI — set ARKADE_HEROES_EMULATOR");
        var esploraApi = Environment.GetEnvironmentVariable("ARKADE_HEROES_ESPLORA") ?? info.EsploraApiUri
            ?? throw new GameClientException("no esplora API for chain time — set ARKADE_HEROES_ESPLORA (e.g. http://localhost:8999/api/v1)");

        var wallet = await WalletAsync();
        Console.WriteLine($"    rebuilding the offer covenant locally (reclaim unlocks at chain time {offer.RefundAfterUnixSeconds})…");
        try
        {
            await Chain.Covenants.OfferReclaimFlow.ReclaimAsync(
                wallet, new Uri(emulatorUri), offer,
                ct => Chain.Covenants.EsploraChainTime.GetMedianTimeAsync(_http, esploraApi, ct));
        }
        catch (Chain.Covenants.RefundNotYetDueException ex)
        {
            Console.WriteLine($"    not yet: reclaim unlocks at chain time {ex.DueUnixSeconds}, chain is at {ex.ChainUnixSeconds} — try again later");
            return;
        }
        Console.WriteLine("    reclaim co-signed — waiting for the item to return to your wallet…");
        await wallet.WaitForAssetAsync(offer.ItemAssetId, TimeSpan.FromSeconds(90));
        Console.WriteLine($"  ✓ offer {ShortId(offerId)} cancelled — the item is back in your wallet");
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
