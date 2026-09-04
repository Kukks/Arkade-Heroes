# ArkadeHeroes.Sim — playthrough harness

Plays the game at scale against a real in-process server (`WebApplicationFactory<Program>` over the
in-memory chain) and reports what the playerbase was actually able to do. The in-memory chain has real
economics — every player starts on `InMemoryChainService.FaucetSats` and invoices genuinely debit them —
so running out of sats is a real outcome, not a simulation artifact.

Three modes:

```bash
# A populated arena: N personas taking weighted turns across R rounds.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --players 14 --rounds 10 --seed 7

# How fast collectibility arrives, straight from GeneMixer + Rarity.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --rarity --population 4000 --generations 10

# What the XP mint pays, straight from Gauntlet.Resolve.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --xp --samples 4000

# Whether a new player can afford to climb, and what would have to move if not.
dotnet run --project tools/ArkadeHeroes.Sim -c Release -- --afford --budget 100000
```

Everything is seeded, so a finding is reproducible by quoting its seed.

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

**Rarity works; breeding throughput is what gates it.** `--rarity`: non-Common heroes are 5.8% of
generation 1 and 28% by generation 10. The arena run produced 9 births in total, which is why it saw
100% Common — that is a throughput observation, not a fault in the trait system.
