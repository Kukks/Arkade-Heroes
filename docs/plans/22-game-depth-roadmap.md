# Game-depth roadmap — scarcity, sinks, and the broader backlog

The core loop (breed → battle → trade → progress) is complete and covenant-enforced.
This doc tracks the next layer: making heroes genuinely **rare, collectible, and
finite**, plus the broader genre gaps surfaced in the 2026-07-05 design review — so
nothing is lost between sessions. Each numbered item is its own spec → plan → build
cycle.

## Hero scarcity & sinks initiative (decomposed 2026-07-05)

Five related pieces sharing one foundation. Design forks resolved with the user:
traits are **hybrid** (mostly cosmetic identity + a ≤5% capped affinity sliver) and
**heritable** (mutation introduces, inheritance propagates, gen-0 blank so all rarity
is breeding-earned).

### 0. Rarity / trait foundation — KEYSTONE — 🎨 SPEC'D (design approved 2026-07-05)

8 dominant/recessive trait categories packed into the reserved genome bytes `[16..31]`;
6 cosmetic + 2 affinity. Traits emerge by mutation and propagate by inheritance
(hidden recessives can surface — the "mewtation" moment). A trustless, genome-derived
**rarity tier** (from expressed traits), shown alongside separate **recessive-potential**
and **provenance** (generation/lineage) signals. A ≤5% affinity nudge stays inside the
deterministic `BattleEngine`. Surfaced in `show` / `mine` / marketplace / a `rarest`
board — all client-computed from the genome, so nothing needs server trust.

Detailed design: `docs/superpowers/specs/2026-07-05-hero-rarity-trait-foundation-design.md`
(working-tree only — superpowers specs are not committed, per repo policy).
**Everything below references "traits" / "rarity" from this foundation.**

### 1. Scaling breed fee — sink — 📋 TODO (independent, small)

The breed fee already EXISTS (`GameOptions.BreedingFeeSats = 1000`, covenant-collected)
but is FLAT. Escalate it with breed count — mirror the doubling cooldown in
`BreedingPolicy.CooldownAfterBreed` — so over-breeding a hero costs progressively more
sats: a real sats sink that complements the escalating cooldown. Independent of the
trait foundation; can ship anytime.

### 2. Sterility — hero sink — ✅ SHIPPED (rarity-derived)

Rather than spend scarce genome space on a fertility gene, sterility is DERIVED from
rarity: `Sterility.IsSterile(genome)` gives each hero a tier-scaled chance of being born
unable to breed (Common 0% → Legendary 50%), rolled deterministically from a
domain-separated hash of the (committed) genome — so it's verifiable, and Common/gen-0
heroes are always fertile. `CommitBreedingAsync` refuses a sterile parent, so the rarest
lines are self-limiting in supply (a sterile Legendary is truly finite — strong
collectible value). Surfaced as `HeroDto.IsSterile` in `show`/`rarest`. No genome or
covenant change. Tests: Sterility unit (bands, deterministic, rolls-both-ways) +
`SterileHero_CannotBreed` integration.

### 3. Hero merging / fusion — hero sink — ✅ SHIPPED (both rungs, covenant live)

Consume a **base + a sacrifice** (2 inputs) to mint ONE hero that CONCENTRATES their
traits toward the rarest — but as a commit–reveal GAMBLE, so it can't trivially defeat
sterility. `Fusion.Fuse(base, sacrifice, entropy)` keeps the base's stats (no
power-creep), and per trait category takes the rarer of the two dominants ~85% of the
time (entropy-seeded); the fused genome — hence its genome-derived rarity + sterility —
is unpredictable, so pushing toward Legendary risks a sterile dead-end ("inbreeding
depression"). Both inputs are BURNED — declared in the asset packet with NO output, so
arkd destroys them (a true sink, not a treasury pile-up); the fused hero inherits the
base's LEVEL (receipt-attested via a `merge` genesis level that `ReplayLevel` seeds from)
and `max(gen)+1`. Rung 1 = escrow/treasury mode, client-audited (`VerifyMerge` recomputes
the fused genome). Rung 2 = the `MergeAuthorized` covenant — reuses breeding's proven gate
(both inputs present, one hero under the species `0xe7`, fee to treasury, oracle sig over
the fused-metadata root `0xe9`+CSFS); execution differs only in the packet (inputs burned
instead of retained to the player, so the layout is breeding's exact 2-output shape). A
flat `GameOptions.MergeFeeSats` sats sink on top. Client `merge <base> <sacrifice>
[covenant]`. Tests: Core `FusionTests` (determinism, concentration, stats-from-base) +
`ReplayLevel` genesis seeding + InMemory `MergeFlowTests` + live `CovenantMergeFlowE2ETests`
(inputs burned + gone from the wallet, fused minted + audited) + escrow rebuildable via
`GET /api/merges/{id}/escrow`. Follow-up (parked): >2 inputs; escalating merge fee. The
non-custodial refund is at breed parity — the timelocked refund LEAF is on-chain and the
escrow-params endpoint serves a trustless rebuild; the only remaining gap is a client
refund COMMAND, a cross-cutting feature NEITHER breed nor merge escrows have yet (only
wager/match escrows do via `refund <matchId>`).

### 4. Hardcore death-match covenant — hero sink + PvP depth — ✅ SHIPPED (both rungs, covenant live)

A winner-takes-all match where BOTH players stake their HERO; consent is structural (you
opt in by staking into your own per-party escrow, reclaimable via a timelocked refund
leaf). Brainstorm decisions (2026-07-05): permadeath is GUARANTEED (the loser's hero
always burns — the truest supply sink; survival-roll + capture rejected); matchmaking is
OPEN-BUT-WARNED (any pairing, but the client shows the level gap + `Matchmaking.Favor`
favored/even/underdog before you stake); a death-match awards NO XP (the reward is the
permakill; the risk is your own hero). The fight is the normal deterministic `BattleEngine`
duel (`FairnessAudit` replays it → the winner is client-verifiable). Rung 1 = escrow/
InMemory, client-audited. **Rung 2 = the LIVE settle covenant:** per-party
`DeathMatchEscrowContracts` (mirrors `WagerEscrowContracts`) with two oracle-signed settle
branches built on a NEW `ArkadeCovenants.SettleAuthorizedNoPot` (`CheckSigFromStackGate` +
`Sha256Gate` + `OP_1` — NO sats `PayTo`, since the "pot" is heroes, routed by the packet;
the `OP_1` leaves the single truthy stack result the arkade VM requires) + a timelocked
refund leaf; `NArkChainService.SettleDeathMatchAsync` spends BOTH per-party escrows in one
emulator-co-signed tx whose asset packet passes the winner's hero through to the winner and
BURNS the loser's hero (input, no output — the merge true-burn). Client
`deathmatch`/`accept-death`/`settle-death` with the pre-stake burn warning. Tests:
`MatchmakingTests.Favor` + InMemory `DeathMatchFlowTests` + live `CovenantDeathMatchE2ETests`
(loser hero gone from every wallet, winner retained, client-verifiable). **Follow-ups
(parked):** covenant-staked GEAR transfer to the winner (the chosen spoil — deferred to
keep rung 2 focused on the novel two-escrow burn/return; gear = fungible item units, staked
+ routed in the settle packet); the trait absorb (v2 — forces re-minting the winner since
genomes are immutable); a client-facing death-match refund command; the structural
`INSPECTOUTASSETLOOKUP` unification (script-enforce the burn/return, shared with breed/
merge/offer).

### Parked ideas (from the 2026-07-05 brainstorm — don't lose)

- **Named "fancy" sets** — specific trait COMBINATIONS earn a special title. A strong
  collectible hook, but a content catalog of combos; layers cleanly on the foundation.
- **Affinity-off-in-ranked lever** — if the ≤5% affinity nudge ever feels pay-to-win in
  wagered/ranked play, gate affinities to cosmetic-only there (on in friendly/PvE).
- **Fold generation into the rarity score** — provenance is currently shown SEPARATE
  from the trait-rarity tier; switch to one blended headline number if preferred.

## Broader design backlog (from the 2026-07-05 gap review — not yet scoped)

Genre gaps raised but not chosen for this initiative. Captured so they're not lost;
each would be its own brainstorm.

- **PvE content** — the game is entirely PvP + trade; no adventure/dungeon/boss loop
  for solo play, onboarding, or a reward/XP source that doesn't need a live opponent.
  (Highest-impact content gap. A deterministic PvE ladder reuses `BattleEngine`.)
- **Team / party combat** — combat is 1v1 (`BattleEngine.Fight(challenger, defender)`);
  3v3 + element/class synergies is where the genre's strategic depth lives.
- **Seasons / ranked ladder / tournaments** — the leaderboard is flat + all-time; no
  renewable competitive goal. Matchmaking + receipts + covenant escrows are ready
  substrate for a tournament with a covenant-escrowed prize pool.
- **Hero rentals / scholarships** — lend a hero, split earnings, enforced by a rental
  COVENANT (returns after N time). Axie's growth engine; uniquely suited to this game's
  covenant strength; solves onboarding cost.
- **Daily engagement loop** — stamina/energy or daily quests/streaks for retention.
- **Itemization depth** — equipment is a small fungible catalog, one slot-unit per hero;
  no rarity tiers, crafting, or upgrading to chase.
- **Replay/spectator social** — fights are deterministically replayable (receipts);
  sharing/spectating a win is nearly free to build and a natural viral loop.
