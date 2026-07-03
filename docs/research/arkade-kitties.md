# ArkadeKitties contract design — research notes

Extracted 2026-07-03 from read-only clones under `C:\Git\Arkade-Heroes-research` (repos: `arkade-os/compiler`, `ArkLabsHQ/arkade-assets`). Two sources of truth exist and they diverge: the **compiled example** (`compiler/examples/arkade_kitties.{ark,hack,json}`) is a simplified, actually-compiling contract; the **design doc** (`compiler/docs/ArkadeKitties.md`) describes a richer commit-reveal scheme that is NOT compiled. Flagged per claim below.

## Kitty state on-chain

- Each Kitty is a **non-fungible Arkade Asset, amount = 1**, identified by an AssetId pair `(genesis_txid: bytes32, group_index: u16)` — never a single hash (`arkade-assets/arkade-assets.md:15-28`; `compiler/docs/arkade-script-with-assets.md:24`).
- Genes live in **immutable genesis metadata**, committed as a Merkle root in the asset group's `metadataHash` field — set at genesis, never mutable (`arkade-assets/arkade-assets.md:50-94`; `compiler/docs/ArkadeKitties.md:26-39`).
- Committed keys are only `generation` and `genome` (32 bytes). Visual traits derive off-chain from `genome` (`compiler/docs/ArkadeKitties.md:30-39,299-322`).
- Kitties genome byte map (32 bytes): 0-2 Body Color, 3-5 Pattern Color, 6-8 Eye Color, 9-10 Body Pattern, 11-12 Eye Shape, 13-14 Mouth/Nose, 15 Cooldown, 16-31 reserved (`compiler/docs/ArkadeKitties.md:307-322`).
- State lives in the **Arkade Asset V1 packet embedded in exactly one OP_RETURN output** (`OP_RETURN "ARK"(0x41524b) || TLV`, asset record type `0x00`) — not in taproot leaves, not in the VTXO script (`arkade-assets/arkade-assets.md:96-124,222-264`). Ownership is enforced by the VTXO's taproot script; asset accounting is orthogonal to BTC amounts.
- All Kitties share one **Species Control asset**; each Kitty's group sets `control` to that asset's AssetId — this gates minting (`compiler/docs/ArkadeKitties.md:15-19`).

## Breeding

**Compiled version** (`compiler/examples/arkade_kitties.ark:25-72`) — single `breed(...)` function:

- Groups: Sire, Dame, Species Control (all `delta == 0`, retained) + fresh Child.
- Child checks: `isFresh == 1`, `delta == 1` (mint exactly one), `controlIs(species)`, `metadataHash == expectedChildMetadataHash`.
- Outputs: child, sire, dame, control each asserted present via `tx.outputs[idx].assets.lookup(...) == 1`.
- **Child genome is NOT derived on-chain**: the client passes `expectedChildMetadataHash`; contract verifies parents' metadata hashes and an `oracleSig` via `checkSig(oracleSig, oraclePk)`. Randomness = one oracle signature; no commit-reveal in the compiled artifact.

**Design-doc version** (`compiler/docs/ArkadeKitties.md:134-284` — aspirational): `BreedCommit` + `BreedReveal` contracts, commit-reveal + oracle VRF. `mixGenomes(genomeA, genomeB, entropy)` does on-chain trait-by-trait crossover via an unrolled 32-byte mask, with a 1/256 mutation branch (`entropy[31]==0` → `sha256(A+B+entropy)`). Entropy = `sha256(salt + oracleRand)`; oracle must be a VRF to prevent grinding. `computeChildGeneration = max(parents)+1`. Refund path reclaims parents after `tx.locktime >= expirationTime`.

## Arkade-specific opcodes used (compiled ASM, `arkade_kitties.json:83-320`)

| Opcode | Purpose |
|---|---|
| `OP_FINDASSETGROUPBYASSETID` | locate group by `(txid, gidx)`; returns `k 1 \| 0 0` flag |
| `OP_INSPECTASSETGROUPCTRL` | control AssetId of a group (`controlIs`) |
| `OP_INSPECTASSETGROUPMETADATAHASH` | genome/metadata Merkle root |
| `OP_INSPECTASSETGROUPASSETID` + `OP_TXHASH` + `OP_EQUAL` | `isFresh` check (assetId.txid == this tx) |
| `OP_INSPECTASSETGROUPSUM` (src 1/0) + `OP_SUB64` | `delta = sumOut − sumIn` idiom |
| `OP_INSPECTOUTASSETLOOKUP` | asset amount at a given output |
| `OP_INSPECTOUTPUTSCRIPTPUBKEY` | bind destination in `transfer` |
| `OP_CHECKSIG` | oracle/owner sig |

Full opcode tables: `arkade-assets/arkade-script.md:9-177`, `compiler/docs/arkade-script-with-assets.md`, `compiler/docs/tapscript_opcodes.md:38-71` (Elements-style 64-bit + introspection set).

## Ownership / transfer

`transfer(...)` (`arkade_kitties.ark:75-90`): `isFresh == 0`, `controlIs(species)`, `delta == 0`, kitty at `tx.outputs[0]`, `checkSig(ownerSig, ownerPk)`, and `tx.outputs[0].scriptPubKey == new SingleSig(newOwnerPk, exit)` — ownership is the destination VTXO's taproot script. `new SingleSig(...)` compiles to a `<VTXO:SingleSig(...)>` placeholder the Arkade operator resolves at runtime (`compiler/README.md:184-205`).

## Early spec vs current compiler — drift to respect

- **AssetId shape**: early = single `assetId`; current = `(txid, gidx)` pair with `find(txid, gidx)` / `controlIs(txid, gidx)`.
- **find/control convention**: early returns `-1 | gidx` sentinel; current returns trailing success flag `k 1 | 0 0`.
- **Metadata Merkle hashing**: early uses BIP-341-style tagged hashes (`ArkadeAssetLeaf`/`ArkadeAssetBranch`, leaf_version 0x00, lexicographic sort) per the asset spec; current doc simplified to plain `sha256(genLeaf + genomeLeaf)`. **Prefer the tagged-hash tree** (asset-spec) for Arkade Heroes.
- **Contract structure**: docs describe BreedCommit/BreedReveal; the compiled example collapses to one contract with plain oracle checkSig. Treat mixing/VRF as design intent, the `.ark` as the working baseline.

## Compiled artifact JSON shape (what an SDK consumes)

- `contractName`; `constructorInputs[] {name,type}` (baked into scripts — e.g. `speciesControlIdTxid`, `oraclePk`, `exit`).
- `functions[]`: `{name, arkade?, leaves[]}` — `arkade.{inputs[], asm[]}` is the emulator-run covenant with `<placeholder>` tokens; `leaves[]` are L1 tapleaves. Default collaborative leaf = `<SERVER_KEY> OP_CHECKSIGVERIFY <EMULATOR_KEY:fn> OP_CHECKSIG`; witness entries carry `encoding: "schnorr-64"`, `injected: true`.
- `source` (full `.ark`), `compiler {name, version}`, `updatedAt`, `warnings[]`.

## Fees / dust / registry

- Compiled contract has no fee/dust/registry logic. Design doc's `BreedCommit` enforces a fee output as anti-spam (intent only).
- No on-chain registry: authenticity = **Proof of Genesis / Proof of Control** (BIP322 over genesis-input or control-asset UTXOs); an off-chain indexer maintains canonical asset state with reorg rollback (`arkade-assets/arkade-assets.md:339-394`). Spending asset-carrying VTXOs with no ARK OP_RETURN **burns** them (`arkade-assets.md:124`).
