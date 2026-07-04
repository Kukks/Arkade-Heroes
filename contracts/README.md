# Covenant contracts

These Arkade Script sources are the **authoritative specification** of Arkade Heroes' game actions. Every on-chain action the server performs today is shaped to satisfy these covenants, so switching enforcement from "server promises + client audit" to "emulator co-signing" changes *who refuses an invalid transaction*, never the transactions themselves.

| Contract | Governs | Modeled on |
|---|---|---|
| `arkade_heroes.ark` | Hero breeding (parents retained, child fresh-minted with oracle-attested genome) and transfer | `arkade-os/compiler` `examples/arkade_kitties.ark` (compiled baseline) |
| `wager_escrow.ark` | Wagered matches: atomic two-stake settle to the winner, time-locked forfeit/refund | `ArkLabsHQ/coinflip` escrow taptree (`docs/PROTOCOL.md`) |
| `item_offer.ark` | Non-interactive item sales (pay-seller-in-same-tx), reusable for player-to-player trading | `arkade-os/banco` offer covenant |

## How they bind (enforcement model)

Covenant leaves are ordinary multisigs where the emulator's key is **tweaked by the script hash**:

```
scriptHash = tagged_hash("ArkScriptHash", script)
tweakedKey = emulatorKey + scriptHash·G
```

The script bytecode itself travels in an **Emulator Packet** (TLV `0x01` inside the ARK extension OP_RETURN). The emulator (regtest: `http://localhost:7073`) runs the script in its Arkade VM and produces its signature **only if the predicate holds** — the signature is the proof. C# primitives for this live in `src/ArkadeHeroes.Chain/Covenants/` (`ArkadeScriptTweak`, `EmulatorClient`).

## Status

- **All three contracts compile** with `arkade-os/compiler` (`arkadec`); artifacts are committed under `contracts/build/`. Rebuild: `arkadec contracts/<name>.ark -o contracts/build/<name>.json`. The `uint64le` vs `scriptnum` implicit-conversion warnings match the compiler's own shipped kitties example.
- **The enforcement pipeline is proven on regtest** (`CovenantSpendTests` + `CovenantProbe`): covenant VTXO (script-tweaked emulator key + operator key leaf) funded from a self-custody wallet, spent through the emulator with the packet attached — passing script co-signed, failing script refused.
- **The first game covenant is live** (`OfferFulfillCovenantTests` + `ArkadeArtifactContract`/`ArkadeCovenants.PayTo`): a resting offer VTXO fulfillable by anyone who pays the seller the exact ask in the same tx — underpayment refused by the emulator, honest fulfillment co-signed; the taker needs no prior funds and no seller interaction. This is the banco pattern the item marketplace builds on.
- **The wager escrow is live and fully oracle-authorized** (`WagerEscrowCovenantTests` + `ArkadeCovenants.{CheckSigFromStackGate,Sha256Gate,AtomicSweep,SettleAuthorized}`): two players stake into one escrow whose settle branches each pin (a) a per-branch settle message `SHA256("arkade-heroes-settle-v1|matchId|branch")` and the game oracle key via `OP_CHECKSIGFROMSTACK`, (b) the pre-committed server seed, and (c) the atomic both-stakes sweep paying the full pot to that branch's winner. Adversarial E2E: forged oracle signature refused, **cross-branch signature replay refused** (the `.ark` spec's branch-authorization requirement — closed), wrong seed refused, pot siphoning refused; the honest settle sweeps both stakes in one co-signed tx. The oracle key IS the receipt-signing game key, so the authorization players see on-chain matches the ProgressionReceipts they hold.
- **Wire-format trap (fixed)**: the Emulator Packet's INNER fields use Bitcoin CompactSize (`wire.WriteVarInt`), while NArk's `Extension`/`BufferWriter` varints are LEB128 (correct for the outer TLV). The encodings coincide below 128 and silently diverge above — surfaced as emulator-side "unexpected EOF"/garbage execution the moment settle scripts crossed 127 bytes. `EmulatorPacket` writes CompactSize internally; a unit vector pins it.
- **Artifact-format note**: `arkadec`'s current JSON emission places function-arg tokens inline in `arkade.asm`, while the live emulator + ts-sdk (PR #319 program model) require covenant scripts to be **arg-free** (the tweak binds fixed bytes; args ride the EmulatorPacket witness as the VM's initial stack — `engine.SetStack`). Until the compiler emits the program shape, runtime covenants are authored as arg-free bytecode in `ArkadeCovenants` (coinflip's production approach); the `.ark` sources remain the semantic specification.
- The **transaction shapes** (asset groups, deltas, metadata hashes, output bindings) are live today — the NArk-backed server already produces them; these sources pin them down.
- **Grammar findings** (fixed in source, kept for the next contract author):
  - There is **no pubkey-equality predicate** (`pk == otherPk` doesn't parse in `require`). `wager_escrow` therefore settles branch-per-party (`settleToChallenger` / `settleToDefender`, `refundChallenger` / `refundDefender`) with the oracle co-signing only the true branch.
  - There is **no inline arithmetic** in comparisons (`value >= a + b` doesn't parse) — precompute as a constructor param (`potSats`). Both players see the params when funding (the escrow address commits to them), so this stays auditable.
- Oracle roles are interim: breeding/outcome oracles attest **deterministic, publicly recomputable** derivations (commit–reveal seeds are in asset metadata and API responses), so oracle misbehavior is detectable by anyone. The end-state replaces them with in-script derivation + VRF entropy per the ArkadeKitties design doc.
