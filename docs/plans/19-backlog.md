# Backlog after task 18 — priority order under the mandate

Priorities follow the standing mandate: eliminate server-trust surfaces via covenants first, then breadth. Each item lists the validated substrate it builds on, so none starts from conjecture.

## 19. Covenant breeding (the next big rock)

**Trust gap today:** breeding is server-executed with commit–reveal + client `FairnessAudit` recomputation and signed receipts — auditable, but the mint itself is server policy. The covenant should make an invalid breed **unsignable**.

**Validated substrate (directed test 2026-07-04):** the emulator VM (`emulator/pkg/arkade/opcode.go`, image v0.0.3) implements a full asset-introspection opcode family — verified present with handlers wired:
`OP_INSPECTNUMASSETGROUPS(0xe5)`, `OP_INSPECTASSETGROUPASSETID(0xe6)`, `OP_INSPECTASSETGROUPCTRL(0xe7)`, `OP_FINDASSETGROUPBYASSETID(0xe8)`, `OP_INSPECTASSETGROUPMETADATAHASH(0xe9)`, `OP_INSPECTASSETGROUPNUM(0xea)`, `OP_INSPECTASSETGROUP(0xeb)`, `OP_INSPECTASSETGROUPSUM(0xec)`, `OP_INSPECTOUTASSETCOUNT(0xed)`, `OP_INSPECTOUTASSETAT(0xee)`, `OP_INSPECTOUTASSETLOOKUP(0xef)`, `OP_INSPECTINASSETCOUNT(0xf0)`, `OP_INSPECTINASSETAT(0xf1)`, `OP_INSPECTINASSETLOOKUP(0xf2)`.
So a breed covenant CAN structurally enforce, in-script: a child **issuance group** exists with control asset = the species asset, its **metadata hash** equals an expected value, both **parent assets appear in the inputs and are retained in the outputs** (in/out asset lookups), and the breeding fee pays the treasury (`PayTo`).

**Pinned opcode semantics (read from `emulator/pkg/arkade/asset_opcodes.go`, 2026-07-04 — verify against the running image if it gets bumped past v0.0.3).** All of these operate on the tx's **asset packet** (`vm.assetPacket`); if the tx carries no asset packet they raise a script error, so a breed covenant only executes on asset-carrying txs. Stack notation: pops happen top-first, so "pops a, b" means push b first, then a.

| Opcode | Pops (top-first) | Pushes |
|---|---|---|
| `INSPECTNUMASSETGROUPS` | — | group count |
| `INSPECTASSETGROUPASSETID` | k | asset_txid (32B), asset_gidx |
| `INSPECTASSETGROUPCTRL` | k | ctrl_txid, ctrl_gidx, found(1) — or empty,0,0 |
| `FINDASSETGROUPBYASSETID` | asset_gidx, asset_txid | k, 1 — or 0, 0 |
| `INSPECTASSETGROUPMETADATAHASH` | k | 32-byte metadata Merkle root |
| `INSPECTASSETGROUPNUM` | source, k | input count (src 0) / output count (1) / both (2) |
| `INSPECTASSETGROUP` | source, j, k | input: type,[txid if intent],vin,amount · output: 1,vout,amount |
| `INSPECTASSETGROUPSUM` | source, k | input sum / output sum / both — **BigNum-encoded** |
| `INSPECTOUT/INASSETCOUNT` | o / i | asset-entry count at that vout/vin |
| `INSPECTOUT/INASSETAT` | t, o / t, i | asset_txid, asset_gidx, amount (BigNum) |
| `INSPECTOUT/INASSETLOOKUP` | asset_gidx, asset_txid, o / i | amount (BigNum), 1 — or 0, 0 |

Load-bearing details:
- **Fresh-issuance identity** (`resolveAssetID`): a group with no explicit AssetId resolves to `(THIS tx's hash, group index)` — so the child mint is findable/checkable in-script, and parent assets (IDs known at contract-creation time) are located with `FINDASSETGROUPBYASSETID` on their **baked canonical IDs**. Lookups never accept an intent txid in place of the issuance txid.
- **Genome binding without baking the genome** (unknown at funding time — it's committed-then-revealed): make the oracle's CSFS message BE the child group's metadata Merkle root as read from the tx: script = `<childK> INSPECTASSETGROUPMETADATAHASH <oraclePk> CHECKSIGFROMSTACK VERIFY` with the oracle's 64-byte signature in the witness (CSFS pops pk, msg, sig — msg comes from introspection, not the witness). Replay/domain separation rides INSIDE the metadata (an entry like `breed=arkade-heroes-breed-v1|<breedId>|<parentA>|<parentB>` next to the genome entry), so the signed root is context-bound by construction. `OP_CAT` (0x7e) is ALSO enabled in the VM if composed messages are ever preferred.
- **Metadata Merkle root recipe** (`computeMetadataMerkleRoot` / `GenerateMetadataListHash` in `arkd/pkg/ark-lib/asset/metadata.go`): leaf = `SHA256(serialize(entry))`, pairwise `SHA256(left||right)`, odd node promoted unhashed; entry serialization = `varUint(len(key)) key varUint(len(value)) value` where **varUint is Go `binary.PutUvarint` = LEB128 — NOT Bitcoin CompactSize** (`asset/utils.go:28`). This is the exact inverse of the EmulatorPacket trap (inner fields CompactSize): two adjacent layers use OPPOSITE varint flavors. The C# reimplementation MUST have a unit vector with a key/value >127 bytes, or wrong roots ship silently.
- Amounts push as minimally-encoded BigNums — compare against witness numbers encoded the same way (`ArkadeCovenants.EncodeMinimalScriptNum` exists; amount-1 NFT checks just compare to `0x01`).

**Approach (mirrors the proven wager-escrow pattern):**
1. `CovenantProbe` E2E per opcode-group FIRST (the OP_TRUE/OP_FALSE probe pattern from task 13) — pin each table row against the LIVE emulator before composing; the table above is source-read, not yet emulator-executed.
2. Author `ArkadeCovenants.BreedAuthorized(...)`: `Sha256Gate(commitment)` + the metadata-root CSFS gate above + structural checks: child = fresh group with ctrl = species asset (`INSPECTASSETGROUPCTRL`), output sum 1 / input sum 0; each parent found by baked ID with input sum 1 AND output sum 1 (retained, not consumed); breeding fee via `PayTo`. The oracle attests the genome; the **covenant binds the mint to the attestation** — the server cannot mint anything else.
3. Breeding flow gains a covenant mode like matches did (commit 82e430d as the template): breed-escrow address funded by the requesting player; simplest v1 keeps parents in the player's wallet and the covenant checks them as *inputs the player co-signs*, exactly how coinflip treats stakes.
4. InMemory sim enforces the same rules; unit + adversarial E2E (wrong genome root refused, missing/consumed parent refused, wrong control asset refused, fee-theft refused, honest breed passes).

**Pinned (was open question c):** the asset packet travels in the **same ARK extension OP_RETURN** as the EmulatorPacket — the emulator's own tests compose `extension.Extension{assetPacket}` into a single TxOut (`emulator/test/asset_test.go:526`, `addAssetPacketToTx`; issuance packet construction: `createIssuanceAssetPacket`, `test/utils_test.go:717`). The Extension type takes a LIST of packets, and `CovenantSpender` already builds `new Extension([new EmulatorPacket(entries)])` — a breed tx composes BOTH packets in one extension output. Those two emulator test files are the reference recipes for the probe suite.

**Pinned (was open question b):** NArk's asset metadata serialization IS byte-compatible with ark-lib — `AssetGroup.SerializeMetadataList` (NArk.Core/Assets/AssetGroup.cs:157) writes with `BufferWriter.WriteVarInt` = LEB128, same as ark-lib's `PutUvarint`. (The CompactSize divergence bites ONLY the EmulatorPacket's inner fields; asset structures are LEB128 on both sides.) The C# Merkle-root parity vector therefore reuses NArk's `AssetMetadata.SerializeTo` for leaves — still include a >127-byte entry in the vector to lock this in.

**Mostly pinned (was open question a):** `AssetManager.IssueAsync` (NArk.Core/Services/AssetManager.cs:33-107) constructs issuance through the **same `TransactionHelpers.ArkTransactionBuilder`** CovenantSpender uses — it builds the issuance `AssetGroup` (`assetId: null` = fresh, `controlAsset: AssetRef.FromId(...)`, `outputs: [AssetOutput.Create(vout, amount)]`, metadata list), wraps it `Packet.Create([group])`, and passes `packet.ToTxOut()` alongside the value outputs into `ConstructAndSubmitArkTransaction`. The fresh AssetId derives as `(txHash, 0)` — the .NET twin of the emulator's `resolveAssetID`. So the breed tx is hand-assembled the CovenantSpender way: caller outputs + ONE extension TxOut composing `Extension([assetPacket, emulatorPacket])`, then `ConstructArkTransaction` + emulator submit; lines 79-96 are the copyable recipe for the child-mint group. **The only genuinely unprobed step:** whether arkd + emulator ACCEPT an issuance group in a covenant-leaf spend submitted via the emulator path (arkd-side issuance validation under SubmitTx co-sign) — the first CovenantProbe of the breeding task answers exactly this, before any composition work.

**Packet-composition hazard (pinned from `NArk.Core/Helpers/TransactionHelpers.cs:165-203`):** `ConstructArkTransaction` contains an asset-packet **vin remap** path: if PSBT input order diverges from coin order, it finds the extension output, extracts the ASSET packet, remaps its vins, and **rebuilds that output from the asset packet alone** (`Assets.Packet.Create(remappedGroups).ToTxOut()`) — any OTHER packet sharing that Extension output (i.e. our EmulatorPacket) would be silently dropped. Today this never fires for covenant spends: the builder runs with ShuffleInputs=false (input order stable — every passing multi-input settle E2E implicitly proves it, since `EmulatorPacket` entry vins are never remapped by anyone). For breed txs (asset packet + EmulatorPacket in one tx): compose BOTH into one `Extension([assetPacket, emulatorPacket])`, keep input order deterministic, and make the probe assert the EmulatorPacket survives construction byte-for-byte; if a multi-input covenant tx ever fails leaf validation intermittently, suspect input reordering FIRST.

## 20. XP-as-assets

Progression receipts (task 14) already carry signed level facts; mirror them on-chain as fungible XP asset deliveries to the hero owner's address (supply cap per level curve). Substrate: item-asset issuance/delivery pipeline (lazy supply-1000 pattern in `NArkChainService`) is proven. Low protocol risk; mostly game-flow + tests.

## 21. Marketplace (banco pattern)

Item/hero offers as resting covenant VTXOs fulfillable by anyone paying the ask in the same tx — `ArkadeCovenants.PayTo` + the `OfferFulfillCovenantTests` E2E already prove the primitive (underpayment refused live). Work: offer lifecycle (create/list/cancel via timelocked reclaim — reuse task-17/18 refund machinery), asset-side checks with the 0xe5+ opcodes (asset actually delivered to buyer), client commands, server book (index only — the chain is the book's truth).

## 22. Leaderboard

Server-computed from receipts (anyone can recompute — receipts are public + replayable). No covenant work; pure API + client. Low stakes, do late.

## 23. Wallet-file encryption

`SelfCustodyWallet` stores the mnemonic plaintext in the wallet sqlite (`wallet.db`). Add passphrase-derived encryption (scrypt/AES-GCM) with an unlock prompt in the client. Tracked as an explicit follow-up since the E2E harness needs non-interactive unlock (env passphrase).

## 24. Upstream: report the arkd poisoned-txid bug

`contracts/README.md` invariant #4 documents it; verified on v0.9.9-rc.1 AND master (`internal/core/domain/offchain_tx.go` — `OffchainTxFailed` replay sets sticky `Stage.Failed`, later `Requested`/`Accepted`/`Finalized` events are ignored; a refused-then-retried SubmitTx therefore finalizes at the RPC level while the projections never run: input VTXO stays spendable, outputs never created). Repro recipe lives in this repo's task-17 history (`WagerEscrowCovenantTests` pre-fix retry loop). File against `arkade-os/arkd` with the event-stream trace; note the double-spend hazard. Use the `humanized-external-prs` conventions if opening the issue from a session.

## Parking lot (unprioritized)

Match records: mark covenant matches cancelled/expired after a refund (server-side bookkeeping only). Pre-built recovery PSBTs handed to clients at stake time (coinflip's model — lets a dumb watchtower reclaim without rebuilding contracts). CI (GitHub Actions: unit always, E2E behind a regtest service container). VRF entropy replacing commit–reveal per the ArkadeKitties end-state design.
