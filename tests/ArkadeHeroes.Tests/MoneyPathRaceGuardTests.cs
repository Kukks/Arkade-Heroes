using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using Covenants = ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Money-path double-execute races: every once-only guard (daily claimed, starters claimed, match
/// resolved, session completed) must be checked and latched atomically around its NON-IDEMPOTENT
/// chain effect (payout / mint / delivery). The client poll-retries, so concurrent same-key requests
/// are the normal case, not an edge: N tasks released on one barrier must land the effect EXACTLY
/// once. Fresh factory per test — the treasury and store are global singletons.
/// </summary>
public class MoneyPathRaceGuardTests
{
    private const int Racers = 64;

    /// <summary>Releases <paramref name="racers"/> calls of <paramref name="action"/> on one barrier and
    /// counts how many completed without throwing (the losers must hit the flow's already-done rule).</summary>
    private static async Task<int> RaceAsync(int racers, Func<Task> action)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, racers).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try { await action(); return true; }
            catch { return false; }
        })).ToList();
        start.SetResult();
        return (await Task.WhenAll(tasks)).Count(won => won);
    }

    [Fact]
    public async Task ConcurrentDailyClaims_PayTheFaucetExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen();
        var (alice, dto) = await factory.RegisterAsync("Race-Daily");
        await alice.ClaimStartersAsync();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(10_000);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[dto.PlayerId];
        var expected = (await alice.Daily.StatusAsync()).ClaimableNowSats;   // one base reward, no quests
        Assert.True(expected > 0);
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        var wins = await RaceAsync(Racers, () => svc.ClaimDailyAsync(player, CancellationToken.None));

        Assert.Equal(1, wins);                                                         // one claim consumed the day
        Assert.Equal(expected, store.TreasuryOutflowByTag.GetValueOrDefault("daily")); // paid once, not per racer
        Assert.Equal(treasuryBefore - expected, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task ConcurrentStarterClaims_MintExactlyTwoHeroes()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (_, dto) = await factory.RegisterAsync("Race-Starters");
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[dto.PlayerId];

        // Heroes are bought now, so the race needs a cleared invoice behind it. The subject is
        // unchanged: concurrent claims must mint ONE pair, never four — the fee is not the gate here.
        var fee = await svc.RequestStartersAsync(player, CancellationToken.None);
        ((InMemoryChainService)factory.Services.GetRequiredService<IChainService>())
            .PayInvoiceFromPlayer(player.Id, fee!.InvoiceId);

        var wins = await RaceAsync(Racers, () => svc.ClaimStartersAsync(player, CancellationToken.None));

        Assert.Equal(1, wins);
        // One paid invoice, one batch — however many claims raced for it.
        Assert.Equal(StarterPolicy.HeroCount, store.Heroes.Values.Count(h => h.OwnerId == player.Id));
        Assert.True(player.StarterClaimed);
    }

    [Fact]
    public async Task ConcurrentFightResolves_PayThePotAndMoveXpExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Fight-A");
        var (bob, _) = await factory.RegisterAsync("Race-Fight-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);   // covers even a double pot, so an over-payout shows instead of faulting

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id, 1000, "invoice"));
        await alice.PayInvoiceAsync(open.StakeInvoice!.InvoiceId);
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.PayInvoiceAsync(accept.StakeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.FightAsync(player, open.MatchId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("wager"));            // the 2×stake pot, once
        Assert.Equal(1, store.ReceiptsByHero[aliceHero.Id].Count(r => r.Id == open.MatchId)); // XP transferred once
    }

    [Fact]
    public async Task ConcurrentRenameConfirms_GiveOneNameToExactlyOneHero()
    {
        // A hero name is sold as GLOBALLY UNIQUE and costs real sats, so two owners holding the same one is
        // both a broken promise and a double charge. ConfirmRenameAsync re-checks uniqueness at apply time,
        // but the check and the assignment are one check-then-act: released on a barrier, concurrent
        // confirms for the same name can all read "free" before any of them takes it. Measured before the
        // keyed lock, 64 racers handed ONE name to TWO heroes — intermittently, which is exactly why this
        // needs a barrier test rather than a sequential one.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.Configure<GameOptions>(o => o.HeroRenameFeeSats = 0)));
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();

        var contenders = new List<(Player Player, string HeroId)>();
        for (var i = 0; i < Racers; i++)
        {
            var (client, dto) = await factory.RegisterAsync($"Race-Rename-{i}");
            var hero = (await client.ClaimStartersAsync())[0];
            contenders.Add((store.Players[dto.PlayerId], hero.Id));
        }

        // Every contender legitimately passes the REQUEST-time check: nobody holds the name yet.
        const string wanted = "Solstice Vanguard";
        foreach (var (player, heroId) in contenders)
            await svc.RequestRenameAsync(player, heroId, wanted, CancellationToken.None);

        var claimed = await RaceAsync(Racers, async () =>
        {
            var next = contenders[Interlocked.Increment(ref _renameCursor) % contenders.Count];
            await svc.ConfirmRenameAsync(next.Player, next.HeroId, CancellationToken.None);
        });

        // Exactly one hero may end up wearing it, and the losers must be refusals rather than silent
        // overwrites — so the count of heroes holding the name is the assertion, not the success count.
        Assert.Equal(1, store.Heroes.Values.Count(
            h => string.Equals(h.Name, wanted, StringComparison.OrdinalIgnoreCase)));
        Assert.True(claimed >= 1, "at least one confirm must land, or the name was never claimable");
    }

    private int _renameCursor = -1;

    [Fact]
    public async Task TournamentPrize_OnePayoutFailing_StillPaysTheRestOfThePodium()
    {
        // The podium pays each place in a loop and a dropped prize is never retried (a documented v1
        // limit), so the ONE thing that must hold is isolation: losing rank 1's sats must not also cost
        // rank 2 theirs. Nothing exercised this before — the pre-existing fault seam only matched
        // `wager-pot:`/`squad-pot:` memos, so no test had ever faulted a tournament or season payout.
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        chain.Inner.FundTreasury(200_000);   // generous, so the injected fault is the ONLY way a payout can fail

        const long buyIn = 1_000;
        var entrants = new List<(ArkadeHeroesClient Client, string PlayerId, string HeroId)>();
        for (var i = 0; i < 4; i++)
        {
            var (client, dto) = await factory.RegisterAsync($"Race-Tourney-{i}");
            var hero = (await client.ClaimStartersAsync())[0];
            entrants.Add((client, dto.PlayerId, hero.Id));
        }

        // Buy-ins are cleared straight on the sim rather than through /api/dev/pay-invoice: that endpoint
        // downcasts IChainService to InMemoryChainService, which this decorator is not.
        var open = await entrants[0].Client.Tournament.OpenAsync(
            new OpenTournamentRequest(entrants[0].HeroId, buyIn, 4));
        chain.Inner.PayInvoiceFromPlayer(entrants[0].PlayerId, open.BuyIn.InvoiceId);
        for (var i = 1; i < 4; i++)
        {
            var join = await entrants[i].Client.Tournament.JoinAsync(
                open.Tournament.Id, new JoinTournamentRequest(entrants[i].HeroId));
            chain.Inner.PayInvoiceFromPlayer(entrants[i].PlayerId, join.BuyIn.InvoiceId);
        }

        // Rank 1's prize faults. Rank 2's must still land, and the bracket must still resolve.
        chain.FailNextTournamentPrize = true;
        var resolved = await entrants[0].Client.Tournament.ResolveAsync(
            open.Tournament.Id, new FightRequest("tourney-payout-fault"));

        Assert.Equal("resolved", resolved.Tournament.Status);
        Assert.Equal(2, resolved.Prizes.Count);            // both places are still awarded on paper…
        Assert.Equal(1, chain.TournamentPrizesPaid);       // …and exactly the non-faulted one actually paid

        // The books must record only what really moved — a phantom outflow for the dropped prize would
        // make the treasury look poorer than it is and hide the debt instead of surfacing it.
        var store = factory.Services.GetRequiredService<GameStore>();
        Assert.Equal(resolved.Prizes[1], store.TreasuryOutflowByTag.GetValueOrDefault("tournament"));
    }

    [Fact]
    public async Task FightResolve_PayoutFailure_LeavesMatchRetryable_AndPaysOnce()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Fight-Retry-A");
        var (bob, bobDto) = await factory.RegisterAsync("Race-Fight-Retry-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];
        chain.Inner.FundTreasury(100_000);   // generous, so the injected fault is the ONLY way the payout can fail

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id, 1000, "invoice"));
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.StakeInvoice!.InvoiceId);
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        chain.Inner.PayInvoiceFromPlayer(bobDto.PlayerId, accept.StakeInvoice!.InvoiceId);
        chain.Inner.PayInvoiceFromPlayer(bobDto.PlayerId, accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];
        var xpBefore = (store.Heroes[aliceHero.Id].Level, store.Heroes[aliceHero.Id].Xp,
                        store.Heroes[bobHero.Id].Level, store.Heroes[bobHero.Id].Xp);

        // The pot payout faults AFTER both stakes + fees were verified paid: the match must NOT latch
        // "resolved" (or move XP, or issue a receipt), else the pot is stranded in the treasury, the
        // winner unpaid, and every retry bounces off "Match already resolved."
        chain.FailNextPotPayout = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.FightAsync(player, open.MatchId, "retry-nonce", CancellationToken.None));

        Assert.Equal("accepted", store.Matches[open.MatchId].Status);   // still fightable — the pot is retryable
        Assert.Equal(xpBefore, (store.Heroes[aliceHero.Id].Level, store.Heroes[aliceHero.Id].Xp,
                                store.Heroes[bobHero.Id].Level, store.Heroes[bobHero.Id].Xp));   // no receiptless XP
        Assert.Equal(0, store.TreasuryOutflowByTag.GetValueOrDefault("wager"));   // no phantom outflow tally either
        Assert.DoesNotContain(store.ReceiptsByHero.GetValueOrDefault(aliceHero.Id) ?? [],
            r => r.Id == open.MatchId);

        // And the retry succeeds: same nonce → same deterministic fight → same winner, paid exactly once.
        var fight = await svc.FightAsync(player, open.MatchId, "retry-nonce", CancellationToken.None);
        Assert.Equal(2000, fight.WinnerPayout);
        Assert.Equal("resolved", store.Matches[open.MatchId].Status);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("wager"));               // the pot, once
        Assert.Equal(1, store.ReceiptsByHero[aliceHero.Id].Count(r => r.Id == open.MatchId));    // ONE match receipt
    }

    [Fact]
    public async Task ConcurrentSquadResolves_PayThePotExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Squad-A");
        var (bob, _) = await factory.RegisterAsync("Race-Squad-B");
        var mine = (await alice.ClaimStartersAsync()).Select(h => h.Id).ToList();
        mine.Add((await alice.Dev.MintHeroAsync()).Id);
        var theirs = (await bob.ClaimStartersAsync()).Select(h => h.Id).ToList();
        theirs.Add((await bob.Dev.MintHeroAsync()).Id);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1000, "invoice"));
        await alice.PayInvoiceAsync(open.StakeInvoice!.InvoiceId);
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Squad.AcceptAsync(open.MatchId);
        await bob.PayInvoiceAsync(accept.StakeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.ResolveSquadMatchAsync(player, open.MatchId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("squad"));   // the 2×stake pot, once
        Assert.Equal(1, store.ReceiptsByHero[mine[0]].Count(r => r.Id.StartsWith($"{open.MatchId}:")));   // slot-0 duel scored once
    }

    [Fact]
    public async Task SquadResolve_PayoutFailure_LeavesMatchRetryable_AndPaysOnce()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Squad-Retry-A");
        var (bob, bobDto) = await factory.RegisterAsync("Race-Squad-Retry-B");
        var mine = (await alice.ClaimStartersAsync()).Select(h => h.Id).ToList();
        mine.Add((await alice.Dev.MintHeroAsync()).Id);
        var theirs = (await bob.ClaimStartersAsync()).Select(h => h.Id).ToList();
        theirs.Add((await bob.Dev.MintHeroAsync()).Id);
        chain.Inner.FundTreasury(100_000);

        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1000, "invoice"));
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.StakeInvoice!.InvoiceId);
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.MatchFeeInvoice!.InvoiceId);
        var accept = await bob.Squad.AcceptAsync(open.MatchId);
        chain.Inner.PayInvoiceFromPlayer(bobDto.PlayerId, accept.StakeInvoice!.InvoiceId);
        chain.Inner.PayInvoiceFromPlayer(bobDto.PlayerId, accept.MatchFeeInvoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        // The pot payout faults AFTER both stakes + fees were verified paid: the squad match must NOT
        // latch "resolved" (or score the duels), else the pot is stranded and unretryable.
        chain.FailNextPotPayout = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveSquadMatchAsync(player, open.MatchId, "retry-nonce", CancellationToken.None));

        Assert.Equal("accepted", store.SquadMatches[open.MatchId].Status);   // still fightable — the pot is retryable
        Assert.Equal(0, store.TreasuryOutflowByTag.GetValueOrDefault("squad"));
        Assert.DoesNotContain(store.ReceiptsByHero.GetValueOrDefault(mine[0]) ?? [],
            r => r.Id.StartsWith($"{open.MatchId}:"));                       // no receiptless duel XP

        // And the retry succeeds: same nonce → same deterministic relay → same winner, paid exactly once.
        var resolve = await svc.ResolveSquadMatchAsync(player, open.MatchId, "retry-nonce", CancellationToken.None);
        Assert.Equal(2000, resolve.WinnerPayout);
        Assert.Equal("resolved", store.SquadMatches[open.MatchId].Status);
        Assert.Equal(2000, store.TreasuryOutflowByTag.GetValueOrDefault("squad"));                        // the pot, once
        Assert.Equal(1, store.ReceiptsByHero[mine[0]].Count(r => r.Id.StartsWith($"{open.MatchId}:")));   // slot-0 duel scored once
    }

    [Fact]
    public async Task ConcurrentBreedReveals_MintExactlyOneChild()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Breed");
        var parents = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(parents[0].Id, parents[1].Id));
        await alice.PayInvoiceAsync(commit.Invoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.RevealBreedingAsync(player, commit.BreedingId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(3, store.Heroes.Values.Count(h => h.OwnerId == player.Id));   // 2 parents + ONE child
        Assert.Equal(1, store.Heroes[parents[0].Id].BreedCount);                   // breed bookkeeping applied once
    }

    [Fact]
    public async Task BreedReveal_MintFailure_LeavesSessionOpenForRetry()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Breed-Retry");
        var parents = await alice.ClaimStartersAsync();
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(parents[0].Id, parents[1].Id));
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, commit.Invoice!.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        // The child mint faults AFTER the fee was verified paid: the session must NOT latch Completed
        // (or burn the parents' cooldowns), else the paid fee is stranded with no retry.
        chain.FailNextHeroMint = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevealBreedingAsync(player, commit.BreedingId, "retry-nonce", CancellationToken.None));

        Assert.False(store.Breedings[commit.BreedingId].Completed);   // still open — the fee is retryable
        Assert.Equal(0, store.Heroes[parents[0].Id].BreedCount);      // no cooldown burned on a failed mint

        // And the retry succeeds: the SAME paid session mints the child.
        var (child, _, _, _) = await svc.RevealBreedingAsync(player, commit.BreedingId, "retry-nonce", CancellationToken.None);
        Assert.Equal(player.Id, child.OwnerId);
        Assert.True(store.Breedings[commit.BreedingId].Completed);
        Assert.Equal(1, store.Heroes[parents[0].Id].BreedCount);
    }

    [Fact]
    public async Task ConcurrentGauntletRuns_ResolveExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Gauntlet");
        var hero = (await alice.ClaimStartersAsync())[0];
        var open = await alice.Gauntlet.OpenAsync(hero.Id);
        await alice.PayInvoiceAsync(open.FeeInvoice.InvoiceId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.RunGauntletAsync(player, open.GauntletId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(1, store.ReceiptsByHero[hero.Id].Count(r => r.Id == open.GauntletId));   // XP awarded once
        var delivered = 0UL;   // a full clear delivers ONE item unit; a partial clear none — never two
        foreach (var item in ArkadeHeroes.Core.Equipment.ItemCatalog.All)
            delivered += await chain.GetItemAssetBalanceAsync(player.Id, item.Id, CancellationToken.None);
        Assert.True(delivered <= 1);
    }

    [Fact]
    public async Task GauntletRun_DeliveryFailure_LeavesRunRetryable()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Gauntlet-Retry");
        var hero = (await alice.ClaimStartersAsync())[0];

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        // Stack the deck for a FULL clear (the only path that delivers an item): at level 1 the growth
        // genes are moot, so an all-FF genome pins every VISIBLE gene at the 73-point ceiling — far above
        // any typical gen-0 ghost draw — and wearing the wave-5 ghost's own top-gear loadout matches its
        // gear too. (High LEVEL is the wrong lever: it multiplies growth-gene variance, wave by wave.)
        var maxedGenome = store.Heroes[hero.Id].Genome.Bytes.ToArray();
        Array.Fill(maxedGenome, (byte)0xFF);
        var storeHero = WithGenome(store.Heroes[hero.Id], maxedGenome);
        store.Heroes[hero.Id] = storeHero;
        foreach (var itemId in Gauntlet.GhostGear(Gauntlet.WaveCount))
            storeHero.Equipment.Equip(ArkadeHeroes.Core.Equipment.ItemCatalog.Find(itemId)!);

        var open = await alice.Gauntlet.OpenAsync(hero.Id);
        chain.Inner.PayInvoiceFromPlayer(aliceDto.PlayerId, open.FeeInvoice.InvoiceId);

        // Gauntlet.Resolve is pure + deterministic in (hero, entropy, config), and the run entropy hangs
        // only on the session seed + our nonce — so search nonces OFFLINE for one that full-clears, then
        // run it for real exactly once. Deterministic, no probabilistic looping.
        var session = store.Gauntlets[open.GauntletId];
        var config = factory.Services.GetRequiredService<IOptions<GameOptions>>().Value.ToGameConfig();
        var clearNonce = Enumerable.Range(0, 500).Select(i => $"clear-{i}").FirstOrDefault(nonce =>
            Gauntlet.Resolve(storeHero,
                CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.HeroId, nonce),
                config).WavesCleared >= Gauntlet.WaveCount);
        Assert.NotNull(clearNonce);   // deck stacked above — a full-clearing nonce is all but certain

        // The item delivery faults AFTER the fee was verified paid and the run full-cleared: the session
        // must NOT latch Completed (or burn the cooldown), else the paid fee is stranded — run resolved,
        // item never delivered, every retry refused as "already been run".
        chain.FailNextItemDelivery = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RunGauntletAsync(player, open.GauntletId, clearNonce!, CancellationToken.None));

        Assert.False(store.Gauntlets[open.GauntletId].Completed);   // still open — the fee is retryable
        Assert.Null(storeHero.GauntletCooldownUntil);               // no cooldown burned on a failed delivery

        // And the retry succeeds: same nonce → same deterministic run → same full clear, ONE item delivered.
        var run = await svc.RunGauntletAsync(player, open.GauntletId, clearNonce!, CancellationToken.None);
        Assert.True(store.Gauntlets[open.GauntletId].Completed);
        Assert.NotNull(run.ItemAwarded);
        Assert.Equal(1UL, await chain.GetItemAssetBalanceAsync(player.Id, run.ItemAwarded!, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentMergeReveals_MintExactlyOneFusedHero()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Merge");
        var heroes = await alice.ClaimStartersAsync();
        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.RevealMergeAsync(player, commit.MergeId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);
        Assert.Equal(1, store.Heroes.Values.Count(h => h.OwnerId == player.Id));   // both inputs burned, ONE fused hero
        Assert.Equal(store.Merges[commit.MergeId].FeeSats, store.TreasuryInflowByTag.GetValueOrDefault("merge"));   // fee tallied once
    }

    [Fact]
    public async Task MergeReveal_ExecuteFailure_LeavesSessionOpenForRetry()
    {
        var chain = new FailableChain(new InMemoryChainService());
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(chain)));
        var (alice, aliceDto) = await factory.RegisterAsync("Race-Merge-Retry");
        var heroes = await alice.ClaimStartersAsync();
        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));
        chain.Inner.FundMergeEscrowFromPlayer(aliceDto.PlayerId, commit.MergeId);

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        // The escrow execute faults AFTER the deposit was verified funded: the session must NOT latch
        // Completed (or burn the inputs), else the deposited base + sacrifice + fee sit stranded in
        // escrow until the timelock refund.
        chain.FailNextMergeExecute = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RevealMergeAsync(player, commit.MergeId, "retry-nonce", CancellationToken.None));

        Assert.False(store.Merges[commit.MergeId].Completed);   // still open — the deposit is retryable
        Assert.True(store.Heroes.ContainsKey(heroes[0].Id));    // inputs untouched on a failed execute
        Assert.True(store.Heroes.ContainsKey(heroes[1].Id));
        Assert.Equal(0, store.TreasuryInflowByTag.GetValueOrDefault("merge"));   // no fee tallied for an execute that never landed

        // And the retry succeeds: the SAME funded escrow mints the fused hero and burns the inputs.
        var (fused, _, _, _) = await svc.RevealMergeAsync(player, commit.MergeId, "retry-nonce", CancellationToken.None);
        Assert.Equal(player.Id, fused.OwnerId);
        Assert.True(store.Merges[commit.MergeId].Completed);
        Assert.Equal(fused.Id, store.Merges[commit.MergeId].FusedHeroId);
        Assert.False(store.Heroes.ContainsKey(heroes[0].Id));
        Assert.False(store.Heroes.ContainsKey(heroes[1].Id));
    }

    [Fact]
    public async Task ConcurrentDeathMatchSettles_BurnAndReceiptExactlyOnce()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, aliceDto) = await factory.RegisterAsync("Race-DM-A");
        var (bob, _) = await factory.RegisterAsync("Race-DM-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });

        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var player = store.Players[aliceDto.PlayerId];

        var wins = await RaceAsync(Racers, () => svc.SettleDeathMatchAsync(player, open.DeathMatchId, "race-nonce", CancellationToken.None));

        Assert.Equal(1, wins);                                                       // one settle resolved it; the rest hit "already resolved"
        var session = store.DeathMatches[open.DeathMatchId];
        Assert.True(session.Completed);
        Assert.False(store.Heroes.ContainsKey(
            session.WinnerHeroId == aliceHero.Id ? bobHero.Id : aliceHero.Id));      // the loser's hero burned
        Assert.Equal(1, store.ReceiptsByHero[aliceHero.Id].Count(r => r.Id == open.DeathMatchId));   // settled once → ONE receipt
    }

    [Fact]
    public async Task ConcurrentAbsorbDeathMatchSettles_MintExactlyOneAbsorbedHero()
    {
        // AbsorbChance=255 → the roll (nearly) always fires; a ~1/256 keep roll, or a seeded upset where
        // the trait-carrying defender wins (nothing to absorb), retries with fresh heroes — every attempt
        // still races the guard. The absorb double-execute is the WORST case on this path: TWO absorbed
        // heroes minted from ONE death-match.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "255"));
        var svc = factory.Services.GetRequiredService<GameService>();
        var store = factory.Services.GetRequiredService<GameStore>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (alice, aliceDto) = await factory.RegisterAsync($"Race-Absorb-A{attempt}");
            var (bob, _) = await factory.RegisterAsync($"Race-Absorb-B{attempt}");
            var aliceHero = (await alice.ClaimStartersAsync())[0];
            var bobHero = (await bob.ClaimStartersAsync())[0];
            store.Heroes[aliceHero.Id].Level = 20;                                   // favored — the trait-carrier should lose
            store.Heroes[bobHero.Id] = WithTrait(store.Heroes[bobHero.Id], TraitCategory.Aura, 255); // a Legendary Aura to absorb

            var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id, Absorb: true));
            await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
            await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
            var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
            await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
            await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });

            var player = store.Players[aliceDto.PlayerId];
            var wins = await RaceAsync(Racers, () => svc.SettleDeathMatchAsync(player, open.DeathMatchId, "race-nonce", CancellationToken.None));

            Assert.Equal(1, wins);                                                   // the guard holds whichever way the roll went
            Assert.Equal(1, store.ReceiptsByHero[aliceHero.Id].Count(r => r.Id == open.DeathMatchId));
            var absorbed = store.Heroes.Values.Count(h => h.ParentAId == aliceHero.Id && h.ParentBId == bobHero.Id);
            if (absorbed == 0) continue;   // keep roll / upset — retry with fresh heroes
            Assert.Equal(1, absorbed);     // ONE absorbed hero minted, never two
            return;
        }
        Assert.Fail("expected an absorb mint within 8 attempts at AbsorbChance=255");
    }

    /// <summary>Clones a hero with one dominant trait gene set (mirrors DeathMatchFlowTests.WithTrait) —
    /// starters are blank on traits, so this gives the loser a trait the winner can absorb.</summary>
    private static Hero WithTrait(Hero h, TraitCategory cat, byte value)
    {
        var bytes = h.Genome.Bytes.ToArray();
        bytes[16 + (int)cat * 2] = value;
        return WithGenome(h, bytes);
    }

    /// <summary>Clones a hero with a replacement genome (<see cref="Hero.Genome"/> is init-only) — the
    /// gauntlet retry test pins every stat gene at the ceiling so a full clear is near-certain per nonce.</summary>
    private static Hero WithGenome(Hero h, byte[] genomeBytes) => new()
    {
        Id = h.Id, OwnerId = h.OwnerId, Name = h.Name, Genome = new Genome(genomeBytes),
        Generation = h.Generation, ParentAId = h.ParentAId, ParentBId = h.ParentBId,
        Level = h.Level, Xp = h.Xp, BreedCount = h.BreedCount,
        EntropyHex = h.EntropyHex, ServerSeedHex = h.ServerSeedHex, PlayerNonce = h.PlayerNonce,
        AssetId = h.AssetId, MintArkTxId = h.MintArkTxId,
    };

    /// <summary>Delegates to the real InMemory sim but can fault the NEXT hero mint, merge execute,
    /// pot payout (a `wager-pot:`/`squad-pot:` memo — other payouts pass through, the PayoutProbeChain
    /// pattern), or item delivery — the deterministic stand-in for "the chain call failed after the
    /// deposit was verified paid".</summary>
    /// <remarks>INTERNAL rather than private so the owed-payout record tests can fault the same money paths
    /// through the same seam — a second decorator over the same calls would be a second thing to keep in
    /// step with <see cref="IChainService"/>.</remarks>
    internal sealed class FailableChain(InMemoryChainService inner) : IChainService, ISimulatedChain
    {
        public InMemoryChainService Simulator => inner;
        public InMemoryChainService Inner => inner;
        public volatile bool FailNextHeroMint;
        public volatile bool FailNextMergeExecute;
        public volatile bool FailNextPotPayout;
        public volatile bool FailNextItemDelivery;
        /// <summary>Faults the next `tournament:` prize payout and counts the ones that settle, so a test can
        /// prove a dropped prize does not take the rest of the podium down with it.</summary>
        public volatile bool FailNextTournamentPrize;
        private int _tournamentPrizesPaid;
        public int TournamentPrizesPaid => Volatile.Read(ref _tournamentPrizesPaid);
        /// <summary>Faults the next `season:` prize payout — the third of the three never-retried money paths,
        /// which nothing could fault before.</summary>
        public volatile bool FailNextSeasonPrize;
        /// <summary>Faults the next `tournament-refund:` payout, so a test can prove a stranded entrant's
        /// buy-in is recorded as OWED rather than lost.</summary>
        public volatile bool FailNextTournamentRefund;
        /// <summary>Faults the next paid-check. This is the UNKNOWN case and the reason it is separate from
        /// the two above: the refund path cannot tell whether the buy-in ever cleared, so it must not be
        /// recorded as a debt NOR as a non-debt.</summary>
        public volatile bool FailNextPaidCheck;

        public Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default)
        {
            if (FailNextHeroMint)
            {
                FailNextHeroMint = false;
                throw new InvalidOperationException("Simulated mint fault (injected by test).");
            }
            return inner.MintHeroAssetAsync(toPlayerId, data, ct);
        }

        public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default) => inner.GetInfoAsync(ct);
        public Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default) => inner.RegisterPlayerAddressAsync(playerId, arkadeAddress, ct);
        public Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default) => inner.GetPlayerAddressAsync(playerId, ct);
        public Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default) => inner.GetAddressBalanceSatsAsync(playerId, ct);
        public Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default) => inner.CreateFeeInvoiceAsync(memo, amountSats, ct);
        public Task<FeeInvoice?> GetFeeInvoiceAsync(string invoiceId, CancellationToken ct = default) => inner.GetFeeInvoiceAsync(invoiceId, ct);
        public Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default)
        {
            if (FailNextPaidCheck)
            {
                FailNextPaidCheck = false;
                throw new InvalidOperationException("Simulated paid-check fault (injected by test).");
            }
            return inner.IsInvoicePaidAsync(invoiceId, ct);
        }
        public Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default)
        {
            if (FailNextItemDelivery)
            {
                FailNextItemDelivery = false;
                throw new InvalidOperationException("Simulated item-delivery fault (injected by test).");
            }
            return inner.DeliverItemAssetAsync(toPlayerId, itemId, itemName, ct);
        }
        public async Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default)
        {
            if (FailNextPotPayout && (memo.StartsWith("wager-pot:", StringComparison.Ordinal)
                                      || memo.StartsWith("squad-pot:", StringComparison.Ordinal)))
            {
                FailNextPotPayout = false;
                throw new InvalidOperationException("Simulated pot-payout fault (injected by test).");
            }
            if (FailNextSeasonPrize && memo.StartsWith("season:", StringComparison.Ordinal))
            {
                FailNextSeasonPrize = false;
                throw new InvalidOperationException("Simulated season-prize fault (injected by test).");
            }
            // Checked BEFORE the `tournament:` arm below: a refund memo is `tournament-refund:`, which does
            // not start with `tournament:`, but keeping the two apart by order as well as by prefix means a
            // later rename of either tag cannot quietly make one seam swallow the other's payouts.
            if (FailNextTournamentRefund && memo.StartsWith("tournament-refund:", StringComparison.Ordinal))
            {
                FailNextTournamentRefund = false;
                throw new InvalidOperationException("Simulated tournament-refund fault (injected by test).");
            }
            if (memo.StartsWith("tournament:", StringComparison.Ordinal))
            {
                if (FailNextTournamentPrize)
                {
                    FailNextTournamentPrize = false;
                    throw new InvalidOperationException("Simulated tournament-prize fault (injected by test).");
                }
                var prizeTx = await inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
                Interlocked.Increment(ref _tournamentPrizesPaid);
                return prizeTx;
            }
            return await inner.PayoutAsync(toPlayerId, amountSats, memo, ct);
        }
        public Task<long> TreasuryBalanceAsync(CancellationToken ct = default) => inner.TreasuryBalanceAsync(ct);
        public Task<WagerEscrowInfo> CreateWagerEscrowAsync(string matchId, string challengerPlayerId, string defenderPlayerId, long stakeSats, byte[] seedCommitment32, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateWagerEscrowAsync(matchId, challengerPlayerId, defenderPlayerId, stakeSats, seedCommitment32, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsEscrowFundedAsync(string matchId, CancellationToken ct = default) => inner.IsEscrowFundedAsync(matchId, ct);
        public Task<WagerEscrowFunding?> GetWagerEscrowFundingAsync(string matchId, CancellationToken ct = default) => inner.GetWagerEscrowFundingAsync(matchId, ct);
        public Task<Covenants.WagerEscrowParams?> GetWagerEscrowParamsAsync(string matchId, CancellationToken ct = default) => inner.GetWagerEscrowParamsAsync(matchId, ct);
        public Task<BreedEscrowInfo> CreateBreedEscrowAsync(string breedingId, string playerId, string parentAAssetId, string parentBAssetId, long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateBreedEscrowAsync(breedingId, playerId, parentAAssetId, parentBAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsBreedEscrowFundedAsync(string breedingId, CancellationToken ct = default) => inner.IsBreedEscrowFundedAsync(breedingId, ct);
        public Task<HeroMintResult> ExecuteBreedCovenantAsync(string breedingId, HeroMintData childData, byte[] oracleSignature64, CancellationToken ct = default) => inner.ExecuteBreedCovenantAsync(breedingId, childData, oracleSignature64, ct);
        public Task<Covenants.BreedEscrowParams?> GetBreedEscrowParamsAsync(string breedingId, CancellationToken ct = default) => inner.GetBreedEscrowParamsAsync(breedingId, ct);
        public Task<string> CreateMergeEscrowAsync(string mergeId, string playerId, string baseAssetId, string sacrificeAssetId, long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default) => inner.CreateMergeEscrowAsync(mergeId, playerId, baseAssetId, sacrificeAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds, ct);
        public Task<bool> IsMergeEscrowFundedAsync(string mergeId, CancellationToken ct = default) => inner.IsMergeEscrowFundedAsync(mergeId, ct);
        public Task<HeroMintResult> ExecuteMergeAsync(string mergeId, HeroMintData fusedData, byte[] oracleSignature64, CancellationToken ct = default)
        {
            if (FailNextMergeExecute)
            {
                FailNextMergeExecute = false;
                throw new InvalidOperationException("Simulated merge-execute fault (injected by test).");
            }
            return inner.ExecuteMergeAsync(mergeId, fusedData, oracleSignature64, ct);
        }
        public Task<Covenants.MergeEscrowParams?> GetMergeEscrowParamsAsync(string mergeId, CancellationToken ct = default) => inner.GetMergeEscrowParamsAsync(mergeId, ct);
        public Task<string> CreateDeathMatchJointEscrowAsync(string deathMatchId, string challengerPlayerId, string challengerHeroAssetId, string defenderPlayerId, string defenderHeroAssetId, byte[] seedCommitment32, string oraclePubKeyHex, long refundAfterUnixSeconds, IReadOnlyList<string>? challengerGearItemIds = null, IReadOnlyList<string>? defenderGearItemIds = null, bool absorb = false, string speciesId = "", CancellationToken ct = default) => inner.CreateDeathMatchJointEscrowAsync(deathMatchId, challengerPlayerId, challengerHeroAssetId, defenderPlayerId, defenderHeroAssetId, seedCommitment32, oraclePubKeyHex, refundAfterUnixSeconds, challengerGearItemIds, defenderGearItemIds, absorb, speciesId, ct);
        public Task<bool> IsDeathMatchEscrowFundedAsync(string deathMatchId, CancellationToken ct = default) => inner.IsDeathMatchEscrowFundedAsync(deathMatchId, ct);
        public Task<string> SettleDeathMatchAsync(string deathMatchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleDeathMatchAsync(deathMatchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<HeroMintResult> SettleDeathMatchAbsorbMintAsync(string deathMatchId, bool challengerWon, HeroMintData absorbedData, byte[] serverSeed, byte[] outcomeSignature64, byte[] rootSignature64, CancellationToken ct = default) => inner.SettleDeathMatchAbsorbMintAsync(deathMatchId, challengerWon, absorbedData, serverSeed, outcomeSignature64, rootSignature64, ct);
        public Task<Covenants.DeathMatchJointEscrowParams?> GetDeathMatchEscrowParamsAsync(string deathMatchId, CancellationToken ct = default) => inner.GetDeathMatchEscrowParamsAsync(deathMatchId, ct);
        public Task<OfferInfo> CreateOfferAsync(string offerId, string sellerPlayerId, string itemId, long askSats, long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default) => inner.CreateOfferAsync(offerId, sellerPlayerId, itemId, askSats, refundAfterUnixSeconds, feeSats, ct);
        public Task<OfferInfo> CreateHeroOfferAsync(string offerId, string sellerPlayerId, string heroAssetId, long askSats, long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default) => inner.CreateHeroOfferAsync(offerId, sellerPlayerId, heroAssetId, askSats, refundAfterUnixSeconds, feeSats, ct);
        public Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default) => inner.IsOfferFundedAsync(offerId, ct);
        public Task<bool> WasOfferSoldAsync(string offerId, CancellationToken ct = default) => inner.WasOfferSoldAsync(offerId, ct);
        public Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default) => inner.GetOfferParamsAsync(offerId, ct);
        public Task<string> SettleWagerEscrowAsync(string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default) => inner.SettleWagerEscrowAsync(matchId, challengerWon, serverSeed, oracleSignature64, ct);
        public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default) => inner.VerifyHeroOwnershipAsync(playerId, assetId, ct);
        public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default) => inner.GetItemAssetBalanceAsync(playerId, itemId, ct);
    }
}
