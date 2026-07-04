# MVP completion map — everything remaining for a full, complete game

**Baseline:** `a4f65b8`, tree clean, gate **69 unit + 11 E2E green**. This document is the
definitive remaining-work map for the next agent (Opus), ordered by the standing mandate
(non-custodial ownership first, covenant-enforced fairness first, breadth after). Work each
item top-to-bottom; each has its own definition-of-done so the loop can commit per milestone.

## Definition of MVP-complete (the acceptance bar)

One documented, repeatable **MVP walkthrough E2E** (item 7 below) in which two players, each
with their own self-custody wallet, complete the full life of the game on regtest with every
value-bearing action either covenant-enforced or client-verifiable:

register → fund → claim starters → **covenant breed** (parents retained, child genome
oracle-attested + covenant-pinned, fee paid) → equip → **covenant wagered duel** (oracle-authorized
settle) → abandoned-match **refund** (no server) → hero **transfer** → **marketplace** item sale →
**leaderboard** reflects results → `verify-receipts` passes for both players.

Everything below exists to make that walkthrough pass. What already passes today: wagered
duels (covenant mode, adversarial-tested), refunds (player-facing), transfers, equipment,
receipts. What follows is the gap list.

## 1. Covenant breeding — task 19, rungs 1–6 — ✅ DONE (SHIPPED live)

All six rungs proven on regtest and committed: `BreedAuthorized` covenant gate (parents retained via `0xf2`, child controlled by species via `0xe7`, fee via `PayTo`, oracle CSFS over the `0xe9`-read metadata root — honest breed co-signed, cheats refused); the breed ESCROW game flow (player deposits parents+fee, server assembles the emulator-enforced mint, `GetBreedEscrowParamsAsync` for trustless rebuild); InMemory sim + server `mode: covenant` + live E2E (`CovenantBreedFlowE2ETests` — parents retained, child minted under species, all in the player's wallet). Two hard-won rules pinned in `19-backlog.md`: `0xe7` ctrl_txid is reversed order, and the fee output must pay a distinct address (the builder coalesces same-script outputs). Gate at completion: 74 unit + 14 E2E. **Remaining before MVP-complete: items 2–7 below.** The original rung spec is retained below for reference.

<details><summary>Original rung 4–6 spec (reference)</summary>

### (was) Finish covenant breeding — task 19, rungs 4–6

Rungs 1–3 are PROVEN (see `19-backlog.md` §19: issuance/controlled-issuance under covenants,
metadata-root parity via `ArkadeCovenants.MetadataMerkleRoot`, rows 0xe5/0xe7/0xe9/0xf1 live,
**byte-order rule** — in-VM asset txids are the REVERSE of NArk `AssetId.Txid`; bake
`Txid.Reverse()`).

**Rung 4 — `ArkadeCovenants.BreedAuthorized` + adversarial probes.** Compose from proven
parts, ONE component per live probe run (extend `CovenantBreedProbeTests`; fresh contract
names per attempt; deliver parent assets with `SendAssetAsync`; fund covenant VTXOs before
minting). Script components (witness design: draw the full stack trace in comments first,
like the probe tests do; last witness item = stack top):
- oracle gate: `0xe9` (pops childK, pushes root32) + `<oraclePk32>` push + `0xcc` CSFS
  (pops pk, msg, sig) + `0x69` — the oracle signs the RAW 32-byte metadata root (BIP340 over
  raw message, proven in task 16); domain separation rides INSIDE the metadata (a
  `breed=arkade-heroes-breed-v1|<breedId>|<parentA>|<parentB>` entry next to the genome).
- species pin: `0xe7` (pops childK) + found VERIFY + gidx EQUALVERIFY + txid (INTERNAL byte
  order!) EQUALVERIFY — this is MANDATORY: rung 3 proved arkd lets ANYONE mint under a
  foreign control asset; only this covenant check stops species forgery.
- parent retention ×2: `0xf2` per parent (pops gidx, txid, i — script pushes txid+gidx baked,
  witness supplies i) + found VERIFY + amount==1 EQUALVERIFY; arkd's input-conservation rule
  (rung 2) forces the passthrough groups to exist.
- fee: `PayTo(treasuryP2tr, feeSats)` with a witness output index.
Adversarial probes required: wrong-root signature refused; missing parent refused; wrong
species (uncontrolled or foreign-control child) refused; fee theft refused; honest breed
passes. Done = all six probes green + committed.

**Rung 5 — the breed escrow in the game flow.** Reuse the wager-escrow architecture wholesale
(commit 82e430d as the template; escrow params in KV; shared builder pattern like
`WagerEscrowContracts`):
- Player deposits BOTH parent heroes (via `SendAssetAsync` semantics — the client pays assets
  to the breed escrow address) + the breeding fee sats at a **breed covenant address** whose
  leaves are: `breed` (= `BreedAuthorized`, above) and `refund` (timelocked, parents + fee back
  to the player — task 17/18 machinery reused verbatim: CLTV tapleaf, submit-once, MTP gate,
  client `refund` extends to breed escrows).
- Server (treasury wallet) assembles the breed tx: spends the escrow VTXO through the `breed`
  leaf, packet = [parentA passthrough (back to player), parentB passthrough (back to player),
  child controlled issuance (to player, genome + breed-context metadata)], outputs = player
  carrier + treasury fee + extension. Oracle (= receipt key) signs the child metadata root.
  The covenant makes any OTHER shape unsignable — that is the mandate's payoff.
- Server API: breed already exists (commit–reveal). Add mode `covenant` to the breed request
  (mirroring matches): commit → player funds escrow → reveal computes genome → server executes
  the covenant mint. `GET /api/breedings/{id}/escrow` mirrors the match-escrow endpoint so the
  client can rebuild + refund trustlessly. InMemory sim mirrors every rule (species pin, root
  sig, retention, fee) like `SettleWagerEscrowAsync`'s sim does.
- The genome must live in the CHILD's genesis metadata (it already does for server minting —
  keep byte-compatibility with `FairnessAudit`'s recompute).

**Rung 6 — client + E2E.** Client `breed <a> <b> covenant` flag (auto-fund escrow like
covenant challenges); `refund <breedingId>` (share the refund flow, keyed by escrow params
endpoint); full-game-loop E2E gains the covenant-breed leg; adversarial E2E for the flow.
Done = walkthrough leg passes + client can reclaim an abandoned breed escrow.

</details>

## 2. XP as on-chain assets — task 20 (small, after breeding)

Receipts already carry signed progression; mirror level-ups as fungible XP-asset deliveries
to the hero owner's address. Reuse the item-asset pipeline (lazy issuance in
`NArkChainService`). Client `wallet` already lists assets. Done = duel/breed XP visibly lands
as assets in the winner's wallet in the walkthrough; InMemory parity; receipts remain the
verification root.

## 3. Marketplace — task 21 (medium)

Banco-style offers, primitive already proven live (`OfferFulfillCovenantTests`):
- **Item sale (MVP scope):** seller rests an offer VTXO (item asset + `PayTo(seller, ask)`
  fulfill leaf + timelocked reclaim leaf reusing refund machinery); buyer fulfills in one tx.
  Asset-side check with `0xf0/0xf2` (asset actually delivered) — semantics all pinned.
- Server = index only (`/api/offers` list/create/my-offers); the CHAIN is the truth; client
  `sell <itemId> <price>` / `buyoffer <offerId>` / `cancel <offerId>`.
- **Hero sale is OUT of MVP** (same primitive, more metadata care — parking lot).
Done = walkthrough sells one item between the two players; cancel/reclaim tested.

## 4. Leaderboard — task 22 (small)

Server-computed from receipts (anyone can recompute — that's the trust story): wins, level,
lineage depth. `GET /api/leaderboard` + client `top`. No covenant work. Done = walkthrough
shows both players ranked.

## 5. Wallet-file encryption — task 23 (small, hygiene)

Mnemonic is plaintext in the wallet sqlite. Passphrase-derived key (scrypt/AES-GCM in
`SelfCustodyWallet`), env `ARKADE_HEROES_WALLET_PASSPHRASE` for tests/non-interactive, client
prompts once per session. Done = wallet DB no longer contains the mnemonic in cleartext;
E2Es green with the env passphrase.

## 6. Known warts to fix on the way (each one small; fix when touched)

- **Starter-claim not rolled back on failed mint** (found in task 18): move the
  `StarterClaimed` flag flip AFTER the chain mint succeeds, or roll back on throw; add an
  InMemory unit test. Without this a funding race strands a player hero-less.
- **Match/breeding records after refund**: mark sessions `expired` when an escrow refund is
  observed (server bookkeeping only; the covenant doesn't care) so lists stay truthful.
- **`IsEscrowFundedAsync` exact-amount check** counts ANY exact-stake VTXO — fine today, but
  after marketplace lands, assets at similar values could confuse it; tighten to BTC-only
  VTXOs when touched.

## 7. The MVP walkthrough E2E + runbook (the finish line)

- `tests/ArkadeHeroes.Tests.E2E/MvpWalkthroughTests.cs`: the full two-player story from the
  acceptance bar above, against the real stack, asserting balances/assets/receipts at each leg.
- `docs/RUNBOOK.md`: the same walkthrough as a HUMAN two-terminal script (server + two client
  REPLs, exact commands, expected output) so the user can PLAY it — this is what "playable on
  local regtest" means. README links it.
Done = both exist, walkthrough E2E green in the full gate, runbook manually sanity-checked
against the client's actual command output.

## 8. Post-MVP parking lot (explicitly OUT of scope — do not start before 1–7)

CI (GitHub Actions: unit always; E2E behind a regtest service container), upstream arkd
poisoned-txid bug report (`19-backlog.md` §24 has the trace), hero marketplace, pre-built
recovery PSBTs/watchtower handoff, VRF entropy replacing commit–reveal, XP-weighted
matchmaking, wallet import/restore UX.

## Working rules reminder (unchanged)

Loop protocol per `docs/HANDOFF.md` §1: gate → commit (verify `git show --stat`) → update
`contracts/README.md` + `DESIGN.md` + this file's checkboxes → memory → `TaskUpdate` →
`ScheduleWakeup` ~120s. Read `contracts/README.md` traps + `19-backlog.md` pinned semantics
before ANY chain code. The Fable quota is nearly exhausted — Opus continues from here; when
the limit resets, whichever model wakes follows this same map.
