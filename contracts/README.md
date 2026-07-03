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

## Status and honesty notes

- The **transaction shapes** (asset groups, deltas, metadata hashes, output bindings) are live today — the NArk-backed server already produces them; these sources pin them down.
- `arkade_heroes.ark` sticks to constructs verified in the *compiled* kitties example (`find`, `controlIs`, `metadataHash`, `delta`, `isFresh`, `assets.lookup`, `scriptPubKey`, `checkSig`, `new SingleSig`).
- `wager_escrow.ark` / `item_offer.ark` additionally use `sha256(...)`, `tx.locktime`, `tx.inputs[i].value`, and `tx.outputs[i].value` — each attested in Arkade sources (design docs `compiler/docs/ArkadeKitties.md`, coinflip's covenant builders, emulator opcode set `OP_INSPECTINPUTVALUE`/`OP_INSPECTOUTPUTVALUE`), but these two contracts haven't been run through the compiler yet. Wiring `arkade-os/compiler` in and consuming the emitted artifact JSON is the next covenant milestone; expect mechanical syntax fixes then.
- Oracle roles are interim: breeding/outcome oracles attest **deterministic, publicly recomputable** derivations (commit–reveal seeds are in asset metadata and API responses), so oracle misbehavior is detectable by anyone. The end-state replaces them with in-script derivation + VRF entropy per the ArkadeKitties design doc.
