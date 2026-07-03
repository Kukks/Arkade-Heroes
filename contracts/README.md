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
- The **transaction shapes** (asset groups, deltas, metadata hashes, output bindings) are live today — the NArk-backed server already produces them; these sources pin them down.
- **Grammar findings** (fixed in source, kept for the next contract author):
  - There is **no pubkey-equality predicate** (`pk == otherPk` doesn't parse in `require`). `wager_escrow` therefore settles branch-per-party (`settleToChallenger` / `settleToDefender`, `refundChallenger` / `refundDefender`) with the oracle co-signing only the true branch.
  - There is **no inline arithmetic** in comparisons (`value >= a + b` doesn't parse) — precompute as a constructor param (`potSats`). Both players see the params when funding (the escrow address commits to them), so this stays auditable.
- Oracle roles are interim: breeding/outcome oracles attest **deterministic, publicly recomputable** derivations (commit–reveal seeds are in asset metadata and API responses), so oracle misbehavior is detectable by anyone. The end-state replaces them with in-script derivation + VRF entropy per the ArkadeKitties design doc.
