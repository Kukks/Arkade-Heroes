# Bug report (ready to file) — NArk: the input-remap rebuilds the Extension from the asset packet alone, silently dropping every co-resident packet (including its own EmulatorPacket)

## Summary

`TransactionHelpers.ArkTransactionBuilder.ConstructArkTransaction` corrects asset-packet `vin`
indices when NBitcoin reorders the ark transaction's inputs. The correction rebuilds the whole
Extension `OP_RETURN` output from the **asset packet only**, so any other packet sharing that
envelope is discarded. It also leaves those other packets' own position-dependent fields unremapped.

The most consequential victim is the SDK's own `EmulatorPacket`: a covenant spend whose emulator
packet is dropped reaches the emulator with no script to execute, and is refused.

## Affected

`NArk.Core/Helpers/TransactionHelpers.cs`, at the `needsRemap` block (the
`Assets.Packet.Create(remappedGroups).ToTxOut()` line), as of `NArk.Abstractions/1.0.372-beta`
(`b912bf9`).

## Mechanism

The remap is entered when the built PSBT's input order differs from the coin order — which the
code's own comment says can happen: *"NBitcoin's TransactionBuilder may reorder inputs (e.g. by
amount) even with ShuffleInputs=false."*

Inside, for the one Extension output it finds:

1. `ext.GetAssetPacket()` reads back **only** the asset packet.
2. `Packet.Create(remappedGroups).ToTxOut()` builds a fresh output from that packet alone.
3. `gtx.Outputs[i].ScriptPubKey = remappedTxOut.ScriptPubKey` overwrites the envelope.

Anything else that was in the envelope is now gone. `NArk.Core` merges packets into a single
`OP_RETURN` deliberately (`SpendingService.BuildExtensionOutput` composes the asset packet with
every `ISpendExtensionPacketProvider`'s output, "so the spend stays within the server's
OP_RETURN-output limit"), so co-residency is the normal case, not an exotic one.

`EmulatorPacket.Entries[].Vin` is position-dependent in exactly the same way as
`AssetInput.Vin`, so even preserving the packet without remapping it would be wrong.

## Why this hits the SDK's own covenant path

`AddArkadeEmulator()` registers `ArkadeEmulatorPacketProvider` as an
`ISpendExtensionPacketProvider`. So for any spend that

- carries assets (an asset packet exists to trigger the remap), **and**
- has at least one arkade-bound input (an emulator packet is co-resident), **and**
- gets its inputs reordered,

the emulator packet is dropped before submission and `ArkadeEmulatorSpendSubmitter` sends a
transaction the emulator cannot co-sign. A marketplace-style spend — covenant input + buyer's own
funding coins + an asset passing through — is precisely that shape.

## Expected vs. actual

**Expected:** the remap preserves every packet in the envelope and remaps each one's
position-dependent indices.

**Actual:** the envelope is replaced by an asset-only packet; other packets are lost without
warning. Downstream the failure reads as an emulator refusal ("no emulator packet found"), which
points at the covenant rather than at the builder.

## Suggested fix

Rebuild from the parsed envelope rather than from one packet: keep `ext.Packets`, replace the asset
packet with its remapped form, remap `EmulatorPacket` entry `Vin`s through the same
`inputRemapping`, and re-serialise the whole set. Packets the SDK does not model can at least be
carried through unchanged rather than dropped — and a packet with position-dependent fields the
builder cannot remap should fail loudly instead of silently.

## Impact / current workaround

Arkade Heroes hit this on the buyer-funded marketplace spend and works around it **after**
construction: rebuild the Extension with the EmulatorPacket's vins pointing at each covenant
input's actual checkpoint index, read back the already-remapped asset packet, then re-sign the
funding inputs over the corrected outputs. See `CovenantSpender.SpendManyCoreAsync` in
`src/ArkadeHeroes.Chain/Covenants/CovenantProbe.cs` and the "Buyer/actor-funded covenant-spend
trap" note in `contracts/README.md`.

That workaround is only possible because the game drives `ConstructArkTransaction` directly. A
consumer using the supported `ISpendingService.Spend` path has no seam to correct it from.
