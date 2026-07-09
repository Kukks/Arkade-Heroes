# Bug report (ready to file) — arkd: a refused timelocked offchain tx poisons its txid, so the retry silently drops its VTXOs

> **Status: FILED** as [arkade-os/arkd#1146](https://github.com/arkade-os/arkd/issues/1146) on 2026-07-09.

## Summary

For a **timelocked** offchain transaction, arkd records a failure event under the
submitted txid on every refusal, and its offchain-tx event replay treats that failed
flag as **sticky**. Because a timelocked spend's canonical form is fully deterministic
(locktime = the tapleaf's CLTV, sequence `0xFFFFFFFE` — zero degrees of freedom), a
retry after the timelock matures reuses the **identical txid**. The retry then
"succeeds" at the RPC level (`SubmitTx` + `FinalizeTx` return OK), but the projections
never run: **the input VTXO is never marked spent and the output VTXOs are never
created.** The spend silently evaporates, with no error surfaced anywhere.

The input VTXO staying spendable after an apparently-finalized spend is a
**double-spend hazard**, and the silent success (no error on the retry) makes it hard
to detect.

## Affected

- Verified on **v0.9.9-rc.1** and **master**.
- Source: `internal/core/domain/offchain_tx.go` — the offchain-tx aggregate's event
  replay sets a sticky `Stage.Failed`; once set, later `Requested` / `Accepted` /
  `Finalized` events for the same txid are ignored.

## Why the txid is unavoidably reused

A spend through a timelocked tapleaf is canonicalised by arkd itself: the ark tx gets
`locktime = leaf CLTV` and `sequence = 0xFFFFFFFE`, and the checkpoint must carry the
same locktime. There is no nonce, no change-address freedom, nothing the client can
vary — the pre-timelock (refused) submission and the post-timelock (valid) submission
produce **byte-identical transactions**, hence the same txid. So "just retry with a
different txid" is not possible for the legitimate, deterministic case.

## Reproduction

1. Fund a VTXO spendable via a timelocked tapleaf with CLTV = `T`
   (`CompositeTapScript(LockTimeTapScript(T), NofN(tweakedKey))`).
2. **Before** chain median-time-past reaches `T`, submit the canonical spend. arkd
   refuses it (`FORFEIT_CLOSURE_LOCKED`) and records a `Failed` event under txid `X`.
   `X` is fully determined by the leaf (locktime `T`, sequence `0xFFFFFFFE`).
3. Mine blocks until MTP ≥ `T` (arkd judges the CLTV against chain blocktime, not wall
   clock).
4. Resubmit the **same** canonical spend — necessarily txid `X` again. `SubmitTx` and
   `FinalizeTx` return success at the RPC level.
5. Observe: the input VTXO is still `spent = false`, and the declared output VTXOs were
   never created. Indexer queries on the outpoints
   (`/v1/indexer/vtxos?outpoints=X:0`) show nothing. No error is returned or logged by
   the aggregate; a co-signing emulator logs "finalizing tx" and then goes silent.

A live regression reproduction exists in a downstream project (a covenant wager-escrow
refund E2E, in its pre-fix form that retried the submit): the pre-expiry refusal
poisoned the txid, and the post-expiry retry finalized at the RPC layer while the
projections never ran. The downstream fix was to **submit timelocked spends exactly
once, after the chain clock passes expiry** — a workaround, not a fix for the sticky
flag.

## Expected vs. actual

- **Expected:** a submission that arkd accepts and finalizes creates its output VTXOs
  and marks its input spent — or, if arkd intends to reject a previously-failed txid,
  it returns an explicit error at `SubmitTx`/`FinalizeTx`.
- **Actual:** the retry returns success at the RPC level, but the event replay's sticky
  `Failed` short-circuits the projection, so no state changes. Success is reported,
  nothing happens, and the input remains spendable.

## Suggested fix

In the offchain-tx aggregate's event replay (`internal/core/domain/offchain_tx.go`):

- **Preferred:** do not treat `Failed` as terminal for a txid. Allow a later
  `Requested` / `Accepted` / `Finalized` to supersede a prior `Failed` for the same
  txid — a refusal at time `T1` (timelock not yet mature) should not permanently poison
  a legitimately-acceptable submission at `T2`, especially when the txid is
  deterministic and cannot be varied.
- **At minimum (fail loud):** if a previously-`Failed` txid is intentionally
  non-retryable, reject the resubmission at the RPC layer with a clear error instead of
  returning success while silently dropping the projection. Silent success plus a
  still-spendable input is the dangerous part.

## Impact / current workaround

- **Hazard:** an input VTXO that stays spendable after an apparently-finalized spend is
  a double-spend risk, and the silent success hides it.
- **Scope:** only *timelocked* spends whose deterministic txid forces reuse are exposed
  in practice. Covenant *settlement* spends are unaffected: an emulator/introspector
  refuses covenant violations before arkd ever sees a txid, so the poisoning path isn't
  reached.
- **Workaround (downstream):** never pre-submit a timelocked spend "to test" the
  timelock — submit exactly once, after MTP ≥ expiry. Prove pre-expiry refusal, if
  needed, with a *non-canonical* locktime (`expiry + 1`) so the probe uses a disposable,
  never-to-be-reused txid.
