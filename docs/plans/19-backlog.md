# Backlog after task 18 — priority order under the mandate

Priorities follow the standing mandate: eliminate server-trust surfaces via covenants first, then breadth. Each item lists the validated substrate it builds on, so none starts from conjecture.

## 19. Covenant breeding (the next big rock)

**Trust gap today:** breeding is server-executed with commit–reveal + client `FairnessAudit` recomputation and signed receipts — auditable, but the mint itself is server policy. The covenant should make an invalid breed **unsignable**.

**Validated substrate (directed test 2026-07-04):** the emulator VM (`emulator/pkg/arkade/opcode.go`, image v0.0.3) implements a full asset-introspection opcode family — verified present with handlers wired:
`OP_INSPECTNUMASSETGROUPS(0xe5)`, `OP_INSPECTASSETGROUPASSETID(0xe6)`, `OP_INSPECTASSETGROUPCTRL(0xe7)`, `OP_FINDASSETGROUPBYASSETID(0xe8)`, `OP_INSPECTASSETGROUPMETADATAHASH(0xe9)`, `OP_INSPECTASSETGROUPNUM(0xea)`, `OP_INSPECTASSETGROUP(0xeb)`, `OP_INSPECTASSETGROUPSUM(0xec)`, `OP_INSPECTOUTASSETCOUNT(0xed)`, `OP_INSPECTOUTASSETAT(0xee)`, `OP_INSPECTOUTASSETLOOKUP(0xef)`, `OP_INSPECTINASSETCOUNT(0xf0)`, `OP_INSPECTINASSETAT(0xf1)`, `OP_INSPECTINASSETLOOKUP(0xf2)`.
So a breed covenant CAN structurally enforce, in-script: a child **issuance group** exists with control asset = the species asset, its **metadata hash** equals an expected value, both **parent assets appear in the inputs and are retained in the outputs** (in/out asset lookups), and the breeding fee pays the treasury (`PayTo`).

**Approach (mirrors the proven wager-escrow pattern):**
1. **Pin opcode semantics first** — exact stack args/results from the handler implementations (opcode.go:593+ table → each `opcodeInspect…` func), then a `CovenantProbe` E2E per opcode-group (the OP_TRUE/OP_FALSE probe pattern from task 13) before composing anything. This is how AtomicSweep/CSFS were landed without surprises.
2. Author `ArkadeCovenants.BreedAuthorized(...)`: `Sha256Gate(commitment)` + `CheckSigFromStackGate(breedMessage, gameKey)` (message = `SHA256("arkade-heroes-breed-v1|" + parentA + "|" + parentB + "|" + genomeHash)` — same shape as `SettleMessage`) + asset-structural checks (child group metadata-hash == genome hash from the attested message; parents in+out; fee `PayTo`). The oracle attests the genome; the **covenant binds the mint to the attestation** — the server cannot mint anything else.
3. Breeding flow gains a covenant mode like matches did (commit 82e430d as the template): breed-escrow address funded by the requesting player (fee + parents temporarily under the covenant? — decide: simplest v1 keeps parents in the player's wallet and the covenant checks them as *inputs the player co-signs*, exactly how coinflip treats stakes).
4. InMemory sim enforces the same rules; unit + adversarial E2E (wrong genome hash refused, missing parent refused, fee-theft refused, honest breed passes).

**Open questions to resolve by directed test, not assumption:** (a) exact stack shape of each asset opcode (read the Go handlers); (b) whether issuance (IssuanceParams) transactions flow through the emulator co-sign path the same way spends do — probe with a minimal issuance + packet tx before designing the full flow; (c) how the child's genesis metadata (genome) maps to `metadatahash` (see `arkade-assets` repo spec + `AssetManager.IssueAsync` in NArk).

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
