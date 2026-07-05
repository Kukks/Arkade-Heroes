# VRF entropy vs. commit–reveal — engineering analysis (parking-lot item)

**Decision: KEEP commit–reveal. Do NOT replace it with a VRF.** For *this* game a VRF
is not an upgrade — it would trade away covenant-enforced fairness (the core mandate)
for a client-only check, while the property it adds is already provided. This note
records why, so the decision isn't re-litigated, and scopes where a VRF *could* fit.

## What we have today (and why it's strong)

Fairness is commit–reveal with a player nonce, and — for wagered matches — it is
**pinned by the covenant**:

1. At match open the server generates a 32-byte `serverSeed` and publishes
   `commitment = SHA256(serverSeed)` (`CommitReveal.Commit`). For a covenant match the
   escrow script bakes this commitment in.
2. At fight time the player supplies a fresh `nonce` — which **did not exist when the
   server committed**.
3. `entropy = SHA256(serverSeed ‖ matchId ‖ heroIds ‖ nonce)` (`DeriveEntropy`), and
   `BattleEngine.Fight` is a pure function of it — deterministic, replayable.
4. The server reveals `serverSeed`; anyone checks `SHA256(serverSeed) == commitment`,
   re-derives the entropy, and replays the battle (`FairnessAudit` client-side).
5. **Covenant enforcement:** the wager-escrow settle branch is a `Sha256Gate` over the
   committed seed — revealing the correct seed is what unlocks settlement — and the
   game oracle signs the `(matchId, winner)` message, verified on-chain by
   `OP_CHECKSIGFROMSTACK`. So the payout can only move the way the revealed-seed +
   oracle attestation say. The reveal isn't overhead; it *is* the settle authorization.

Two fairness properties fall out: the server can't bias an outcome (it commits the seed
before the unpredictable nonce exists, so seed-grinding can't target a result), and the
player can't bias one either (the seed is committed before their nonce). Both are
checkable by anyone, and for wagers the money movement is covenant-bound to the same seed.

## What a VRF offers, and why each doesn't land here

A verifiable random function has the server hold a keypair and, per input, produce
`(output, proof)` where `output` is deterministic in `(sk, input)`, unpredictable
without `sk`, and `proof` lets anyone verify `output` against the public key. Its
selling points versus commit–reveal:

- **No reveal round-trip.** — But here the "reveal" is load-bearing: revealing the seed
  is exactly what satisfies the covenant `Sha256Gate` and authorizes the payout.
  Removing the round-trip removes the settle trigger; it's not a cost to optimize away.
- **Anti-grinding (server can't try many seeds).** — Already provided. The server
  commits the seed *before* the player's nonce exists, and the final entropy mixes the
  nonce, so grinding the seed toward a *specific* outcome is futile without predicting
  the nonce. The nonce gives us the same guarantee a VRF would, without new crypto.
- **Unpredictability.** — Also already true: with the seed committed and the nonce
  fresh, neither side can predict the outcome at commit time.

## The disqualifier: the covenant can't verify a VRF

The mandate is covenant-*enforced* fairness. The emulator VM verifies `SHA256`,
`OP_CHECKSIGFROMSTACK` (BIP340), and the asset-introspection family — **not** the
elliptic-curve operations an EC-VRF (RFC 9381) proof needs. So a VRF outcome could only
be checked **client-side**, off-covenant. Replacing commit–reveal with a VRF would
therefore **downgrade** wagered-match fairness from *covenant-enforced* (the money can't
move against the pinned seed) back to *server-policy + client-audit* — precisely the
trust surface tasks 15–17 removed. That is a regression, not progress.

`SHA256(seed)` is covenant-native and cheap; a VRF proof is not verifiable on-chain
here at all. The covenant integration is the whole reason SHA256 commit–reveal was
chosen over fancier schemes in the first place.

## Where a VRF *could* add value (future, not now)

- **If the emulator gains a VRF-verify opcode** (or a pairing/scalar-mult primitive),
  revisit: a covenant could then bind settlement to a VRF proof, and the server's seed
  choice would be provably non-grindable *even in principle* rather than *in practice
  via the nonce*. Marginal gain, but real, and only then covenant-compatible.
- **Defense-in-depth for non-covenant (friendly) fights** via a public beacon: mix a
  future Bitcoin block hash into `DeriveEntropy` so *neither* party controls the
  entropy even before the nonce. This is a small, covenant-neutral change (friendly
  fights have no escrow to break) but costs a block of latency and isn't clearly worth
  it — logged here as an option, deliberately not taken.

## If someone still wants to build it

A hybrid keeps the covenant intact: derive `serverSeed = VRF(sk, matchId)` and still
publish `SHA256(serverSeed)` as the commitment, so the covenant is unchanged and the
VRF proof is an *extra* client-side check that the server didn't grind the seed. Cost:
a full ECVRF implementation (NBitcoin.Secp256k1 has the group ops; RFC 9381 is the
spec) for a property the player nonce already secures. Recommended only if a concrete
threat model shows the nonce is insufficient — none does today.

## Bottom line

The current scheme is *more* aligned with the mandate than a VRF replacement would be:
it is covenant-enforced, grind-proof via the nonce, fully replayable, and already live
and adversarially tested (`WagerEscrowCovenantTests`, `FairnessAudit`). This item is
resolved as a reasoned **no-go**; the buildable options above are parked with their
trade-offs.
