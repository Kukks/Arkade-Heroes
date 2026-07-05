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

### 2. Sterility trait — hero sink — 📋 TODO (needs the foundation)

A heritable trait (a foundation category, or a dedicated gene) that makes a hero
STERILE with some probability, capping a lineage's breeding output — a supply brake.
Open forks: deterministic vs probabilistic; downside-only, or a collectible tradeoff
(a sterile hero is finite → potentially rarer/stronger, so it becomes a deliberate
breeding endpoint). Interacts with rarity: a sterile legendary can never be reproduced.

### 3. Hero merging / fusion — hero sink — 📋 TODO (needs the foundation)

Burn N heroes → one "better" hero: better stats (existing stat/growth genes) + combined
or upgraded traits + a rarity boost. A burn-and-mint COVENANT — the player's own wallet
consumes the input hero assets and mints the fused hero (like breeding, but consuming
the inputs rather than retaining them). Open forks: input count; how stats/traits
combine (best-of / weighted / a fusion roll); species/element constraints; the exact
covenant shape (burn inputs, mint one output under the species).

### 4. Hardcore death-match covenant — hero sink + PvP depth — 📋 TODO (needs the foundation)

A winner-takes-all match where BOTH players stake their HERO. The loser's hero may
PERMANENTLY DIE (burned); the winner gets much more XP, the loser's EQUIPMENT, and
absorbs one of the loser's traits into an open category. Because a hero is a
non-custodial asset, the loser must stake + consent up front — the covenant enforces
"on the oracle-signed settle, the loser's hero (+ gear) burns/transfers to the winner."
Open forks: is death guaranteed or a survival roll?; exactly what transfers (equipment
always; a trait absorb into an empty slot); the covenant (both stake heroes, reuse the
wager-escrow settle shape, enforce burn + transfer). The biggest and most novel covenant
piece — the mandate's showpiece for consent-based, covenant-enforced stakes.

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
