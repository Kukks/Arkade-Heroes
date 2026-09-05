# ArkadeHeroes.Sim — playthrough harness

Plays the game at scale against a real in-process server (`WebApplicationFactory<Program>` over the
in-memory chain) and reports what the playerbase was actually able to do. The in-memory chain has real
economics — every player starts on `InMemoryChainService.FaucetSats` and invoices genuinely debit them —
so running out of sats is a real outcome, not a simulation artifact.

Four modes:

```bash
# A populated arena: N personas taking weighted turns across R rounds.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --players 14 --rounds 10 --seed 7

# How fast collectibility arrives, straight from GeneMixer + Rarity.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --rarity --population 4000 --generations 10

# What the XP mint pays, straight from Gauntlet.Resolve.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --xp --samples 4000

# Whether a new player can afford to climb, and what would have to move if not.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --afford --budget 100000

# Whether a fight is worth watching, straight from BattleEngine.Fight.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --combat --samples 6000

# What CombatConfig's default-off flags would actually change.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --flags --samples 40000
```

The analysis modes (`--rarity`, `--xp`, `--combat`, `--flags`, `--afford`, `--trials`) are pure and reproduce
exactly at a given seed. The **playthrough mode does not**: it boots the real server, whose commit–reveal
entropy is drawn independently of `--seed`, so same-seed runs differ by as much as different-seed ones.
Measured over three runs of `--seed 7`: treasuries of 147,240 / 159,390 / 180,470 and 46 / 45 / 50 living
heroes. Quote a playthrough number only with several seeds behind it, and treat the spread as the error bar.

Personas (Grinder, Breeder, Duelist, Trader, Whale, Casual) exist so the population is not a uniform
blob: a Trader listing heroes gives a Duelist something to buy, and a system no persona reaches shows up
in the report as `NEVER SUCCEEDED`. Outcomes are bucketed three ways — it worked, the game refused it for
a reason a player would understand, or it threw.

## What it measured (2026-09-04)

Numbers below are from this harness at the seeds named; re-run to confirm before relying on them.

**The gauntlet is the only XP mint, and it is a one-to-two wave encounter, not a five-wave one.**
`--xp --samples 4000 --seed 11`, 84,000 runs:

| runner | 0 waves | full clear | avg waves of 5 |
|---|---|---|---|
| recruit (all a new player can own) | 40% | **0.0%** | 1.2 |
| bred, ungeared | 44% | 1.7% | 1.25 |
| bred + best gear at level 1 | 35% | 4.4% | 1.61 |
| bred + best gear at level 5 | 24% | 7.8% | 2.07 |
| bred + best gear at level 10 | 14% | 19.5% | 2.78 |

Two things follow. Gear is the real progression lever and it works — full clears go 0% → 19.5%. But
`dungeons.json` sets `dropRequiresFullClear: true`, so the drop table is unreachable for the entry cohort:
a recruit never cleared five waves in 28,000 attempts (best ever seen: four).

**The level curve costs more than the only XP source can fund.** Same run, `COST TO CLIMB`: an ungeared
recruit needs 214 gauntlet runs to reach the level-10 XP cap, at 770–930 sats entry each — **187,500 sats
against a 100,000 starting balance**. It runs out somewhere in the level 7→8 climb, having spent 112,080.
PvP cannot make up the difference: it only moves XP between heroes, and it is sats-negative for the
population as a whole (the 20-player arena run ended with the treasury up 463,760). This is the finding
that explains the leaderboard: after 150 duels, 45 gauntlets, 56 trials and 28 squad matches, every hero
in the top five was still level 1.

`--afford` re-prices that climb under candidate settings. It is analysis, not advocacy — every row
changes who earns sats:

```
  as shipped:              213 runs     186,970 sats   SHORT by 86,970
  coefficient 45 -> 20       116 runs     101,200 sats   SHORT by 1,200
  base 80 -> 40, coeff 20     98 runs      85,900 sats   affordable
  2.0x xp per run            109 runs      95,650 sats   affordable
  dungeon bonus 250 -> 0     213 runs     133,720 sats   SHORT by 33,720
```

The load-bearing row is the last one: **entry-fee changes alone cannot close the gap.** Even making PvE
free at the door — which `Gauntlet.FeeBonusSats` exists specifically to prevent, since the bonus is what
keeps PvE a treasury sink rather than a faucet — still leaves a climber 33,720 short. Only the curve or
the yield reaches affordability.

**A better genome does not help in PvE.** Ghosts are drawn at the runner's own `StatGeneCeiling`, so a
bred hero faces bred-grade ghosts and clears no more waves than a recruit does (44% vs 40% zero-wave).
Breeding a better hero pays in PvP and pays nothing here.

**Most staked duels in a fresh arena move nothing.** `--players 14 --rounds 10 --seed 7`: 37 of 45 staked
duels transferred zero XP. PvP only moves XP between heroes, and a hero holding none can lose none — so
until a hero has been through the PvE mint, a staked duel costs both players a fee and changes nothing.
Per `LeaderboardBuilder`, a zero-XP win also confers no rank, by design.

**Combat and matchmaking are the part that most clearly works.** `--combat --samples 6000 --seed 13`:

| power gap | favourite wins | avg turns |
|---|---|---|
| 0–9% | 61.2% | 6.2 |
| 20–29% | 88.3% | 5.8 |
| 40–49% | 99.3% | 4.6 |
| 50%+ | 99.9% | 3.0 |
| **matchmade (4 nearest)** | **64.2%** | **6.6** |

`PowerScore` predicts cleanly and monotonically, which is the win-rate check its own doc comment asks
for. The number that matters is the last row: served an opponent the way the game actually suggests
one, the mean gap is 7.5% and the favourite wins **64%** over ~6.6 turns — the better hero usually
wins, so investment means something, but loses often enough to be worth watching. No fight hit the
60-turn cap.

Equal-power pairs are excluded from every favourite-win rate: they have no favourite, so counting one
would be scoring an arbitrary side choice. They are reported separately instead (13 of 6,000 in the
random field, 78 of 6,000 matchmade — ties are likelier there by construction, since the suggestion
list is drawn from the nearest by power).

It also shows how much work matchmaking is doing. In a *random* field, 45% of pairings sit above a
50% gap, where the favourite wins 99.9% in three turns — an execution, not a battle. The suggestion
list is load-bearing for fun, not a convenience.

**The dormant combat flags would buy almost nothing.** `--flags --samples 40000 --seed 21` replays the
same 40,000 matchups under each of `CombatConfig`'s default-off flags (they ship off so replays stay
verifiable; each is waiting on a coordinated client+server release):

| config | favourite wins | avg turns | win rate of the *rarer* hero |
|---|---|---|---|
| default (all off) | 82.9% | 6.3 | 50.6% |
| `ElementAwareSelection` | 83.0% | 6.3 | 50.7% |
| `InnateAbilities` | 82.9% | 6.3 | 51.0% |
| both | 83.0% | 6.3 | 51.1% |

At n=40,000 the 95% interval is about ±0.5pp, so the aggregate rows are flat and `InnateAbilities`'
+0.4pp on rarity sits right at the resolution limit. That matters because `InnateAbilities` exists
specifically *"so rarity/breeding start to matter in the fight, not just on the card"* — and a rarer
hero wins ~51% of fights with it on, against ~50.6% without. For a breeding game, that is the price
of the whole rarity ladder in combat terms.

This is **not** "rarity is broken": the code is explicit that cosmetic rarity carries zero
`PowerScore` weight and touches combat only through the capped (≤5%) affinity modifier, and rarity
still drives collectibility, `/api/rarest` and resale. The finding is narrower — *the flag held back
for a coordinated release does not achieve its stated combat goal*, so if rarity is meant to swing
fights, the lever has to be bigger than this.

Two caveats on the method, stated because they bound the claim. The field is bred **six generations
deep** on purpose: `InnateAbilities` buys proc chances with trait rarity, and a gen-1 hero is ~94%
Common, so a shallow field measures nothing (the first version of this run made exactly that mistake).
And "favourite wins" is a *symmetric* metric, so a flag that improves move-selection quality for both
sides can be invisible in it — the check for that is turn count, which would fall if kills got faster,
and it does not move.

**Rarity works; breeding throughput is what gates it.** `--rarity`: non-Common heroes are 5.8% of
generation 1 and 28% by generation 10. The arena run produced 9 births in total, which is why it saw
100% Common — that is a throughput observation, not a fault in the trait system.
