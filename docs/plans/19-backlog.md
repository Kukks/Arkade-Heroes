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

**Remaining open questions (resolve by directed test, not assumption):** (a) do issuance transactions (`AssetManager.IssueAsync` / `IssuanceParams`) flow through the emulator co-sign path the same way spends do? — probe with a minimal issuance + packet tx before designing the full flow; (b) does NArk expose the metadata list it serializes for issuance (for the C# root-parity vector), and does its serialization match `ark-lib/asset` byte-for-byte? (c) how the asset packet reaches `vm.assetPacket` — same ARK extension as the EmulatorPacket or a separate TLV (grep the emulator for `assetPacket =` / extension parsing).

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
