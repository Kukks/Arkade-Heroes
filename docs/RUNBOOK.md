# Playing Arkade Heroes on regtest — the MVP walkthrough

A complete two-player session on local regtest that exercises the CORE covenant
loop: non-custodial wallets, **covenant breeding** (parents retained, child
minted under the species with an oracle-attested genome), **covenant wagered
duels** (emulator-enforced escrow settlement), **timelocked refunds** (reclaim
an abandoned stake with no server), hero transfers, the receipts-computed
leaderboard, and on-chain XP. Runs in three terminals: one server, two players.

The fuller covenant surface shipped since — **merge** (burn two heroes → one
fused), **hardcore death-match** (winner-takes-all + permadeath; covenant-staked
gear; opt-in **trait-absorb**, e.g. `deathmatch <mine> <theirs> absorb`), the
**hero/item marketplace** (resting offers), and **trustless timelocked reclaim
on all five escrows** — is proven by the E2E suite rather than walked through
here. All API calls now go through the typed `ArkadeHeroes.Client.Sdk`.

Everything below is proven by the E2E suite (`tests/ArkadeHeroes.Tests.E2E`);
this is the same story a human can drive by hand.

## 0. Prerequisites

- Docker running, Node ≥ 18, .NET SDK 10.
- The regtest stack up (arkd + Arkade Script emulator + bitcoin):
  ```bash
  node regtest/regtest.mjs start --profile ark --profile emulator
  ```
  Verify: `docker ps` shows `arkd`, `emulator`, `bitcoin`. Faucet password is
  `secret`. (Ports: arkd `:7070`, emulator `:7073`.)
- Gate is green: `dotnet test tests/ArkadeHeroes.Tests` → 173 passing (plus 50 E2E behind the regtest, `tests/ArkadeHeroes.Tests.E2E`).

## 1. Start the server (NArk mode, covenants on)

```bash
# bash
Chain__Mode=NArk Chain__NArk__AllowTreasuryAutoCreate=true \
  Game__WagerEscrowRefundAfter=00:00:20 \
  dotnet run --project src/ArkadeHeroes.Server
# PowerShell
$env:Chain__Mode="NArk"; $env:Chain__NArk__AllowTreasuryAutoCreate="true"
$env:Game__WagerEscrowRefundAfter="00:00:20"
dotnet run --project src/ArkadeHeroes.Server
```

The server listens on `http://localhost:5210`. `Game__WagerEscrowRefundAfter=00:00:20`
makes the refund demo (step 6) reachable in seconds instead of 24h.

`Chain__NArk__AllowTreasuryAutoCreate=true` is what lets the server generate a treasury
for this throwaway regtest database. Without it a server that finds no treasury recorded
REFUSES to start rather than minting one — because it cannot tell a fresh install from a
database that was lost, and guessing wrong rotates a real treasury to a key nobody has.
Never set it on a deployment holding value.

Fund the treasury (its address is in the server log, or `GET /api/chain/info`):
```bash
node regtest/regtest.mjs ark send --to <treasury tark1…> --amount 400000 --password secret
```

## 2. Two players (separate wallets)

Each player runs their own client with a distinct wallet home. Terminal A:
```bash
ARKADE_HEROES_HOME=./play/alice dotnet run --project src/ArkadeHeroes.Client
```
Terminal B:
```bash
ARKADE_HEROES_HOME=./play/bob dotnet run --project src/ArkadeHeroes.Client
```

In each client:
```
register Alice        # (Bob in terminal B) — binds your self-custody wallet address
wallet                # shows your tark1… address and (zero) balance
```
Fund each player's address from the faucet (address shown by `wallet`/`register`):
```bash
node regtest/regtest.mjs ark send --to <alice tark1…> --amount 100000 --password secret
node regtest/regtest.mjs ark send --to <bob tark1…>   --amount 100000 --password secret
```
Then buy starters in each client. A recruit is BOUGHT — it quotes a fee, pays it from the
wallet you just funded, and mints ONE generation-0 hero — so run it twice for Alice, who
needs a breedable pair below:
```
starter               # quote → pay → one generation-0 hero, minted into your wallet
starter               # again (Alice only): breeding needs two parents
mine                  # list them (note the two hero numbers, e.g. 1 and 2)
```

## 3. Covenant breeding (Alice)

Breed Alice's two recruits under the covenant — the parents are retained and
the child is minted under the species with an oracle-attested genome; an invalid
breed is unsignable:
```
breed 1 2 covenant    # deposits both parents + the fee into the breed escrow,
                      # then reveals; prints 'fairness ✓' and the child hero
mine                  # the two parents are STILL here, plus the new child
```
(Omit `covenant` for the invoice-mode breed — treasury mint, fee invoice.)

## 4. Equip (Alice)

```
shop                  # list items
buy rusty-blade       # delivers one fungible item-asset unit to your wallet
equip 3 rusty-blade   # equip it on hero 3 (the child)
show 3                # sheet shows the equipped weapon
```

## 5. Covenant wagered duel (Alice vs Bob)

Alice challenges Bob for a stake, settled by the emulator-enforced escrow:
```
# Alice:
challenge 3 <bob-hero-id> 5000 covenant   # opens a covenant match; auto-stakes + pays the match fee
matches                                    # note the matchId
# Bob:
accept <matchId>                           # auto-stakes into his own escrow + pays his match fee
# Alice:
duel <matchId>                             # resolves; the pot sweeps to the winner
top                                        # leaderboard now ranks the winner first
```
Both players: `verify-receipts` → every match/breed receipt verifies against the
game key, and each hero's level replays from its receipt chain alone. `me` /
`wallet` show the updated balances; the staked win moved XP from the loser to the
winner — a conserved transfer, so the loser's level can fall. Each fighter also paid a
level-proportional match fee (`500 + 20·level` sats) to the treasury to stage the duel — a
sats sink that makes idle-training a high-level hero cost something every staked fight.

## 6. Timelocked refund (abandoned match)

Show the liveness guarantee — a stake is reclaimable with no counterparty and no
server cooperation:
```
# Alice:
challenge 1 <bob-hero-id> 5000 covenant   # opens + stakes
# ...Bob never accepts. After the refund window (20s here) passes on the chain
# clock, Alice reclaims her own stake straight from the covenant:
refund <matchId>                           # rebuilds the escrow locally, waits
                                           # for chain time, reclaims once
wallet                                     # the 5000 sats are back
```

## 7. Transfer a hero (Alice → Bob)

```
# Alice:
transfer 1 <bob-playerId>                  # your wallet signs; the Arkade asset moves
# Bob:
mine                                       # the transferred hero now appears here
```

## 8. Marketplace — sell an item, buy an offer (Alice → Bob)

Banco-style resting offers: the seller rests one item unit behind a covenant that
pays her the ask; ANYONE fulfils it by paying that exact ask from their OWN wallet
(the emulator refuses underpayment), or the seller reclaims it after the window.
The server is only the discovery index — the buyer rebuilds the offer covenant
locally and can verify its address before paying.
```
# Alice:
buy lucky-feather          # a spare item unit to sell
sell lucky-feather 4000    # rests the offer; deposits the item into its covenant address
offers                     # your resting offer is listed (copy the offerId)
# Bob:
offers                     # discovers Alice's offer
buyoffer <offerId>         # funds the 4000-sat ask from HIS wallet, takes the item
wallet                     # Bob now holds the lucky-feather unit; Alice was paid 4000
# (to cancel an unsold offer instead of selling — after the reclaim window:)
# Alice: canceloffer <offerId>    # rebuilds the covenant locally, reclaims the item
```
Selling a HERO works the same way — the offer covenant is asset-agnostic, so a
character (a unique asset) rests and sells through the identical machinery. After
buying, the new owner claims game-side ownership (the hero moves to them with its
equipment stripped, just like a transfer):
```
# Alice:
sellhero 2 25000           # list hero #2 for sale; deposits the hero asset into the offer
offers                     # your hero offer is listed as kind 'hero' (copy the offerId)
# Bob:
buyhero <offerId>          # funds the 25000-sat ask from HIS wallet, takes + claims the hero
mine                       # the hero now appears among Bob's heroes
```
Regtest note: if `sell` fails with a `VTXO_RECOVERABLE` error, that is a known
arkd/regtest quirk when re-spending a freshly treasury-delivered item VTXO (batch
timing, not the game — mainnet's long horizons avoid it). The buyer-funded
fulfilment itself is covenant-proven live (`CovenantOfferProbeTests`).

## 9. Recovery — lose the machine, keep your heroes (Alice)

The non-custodial promise made concrete: your heroes and funds live in your
wallet, and your 12 words bring them back.
```
# Alice, before:
backup                     # write down the 12 words
# ...the machine dies. On a NEW machine (fresh ARKADE_HEROES_HOME):
restore <the 12 words>     # re-derives the SAME address; your on-chain heroes/funds are back
login                      # signs a server challenge with your wallet → resumes your player
mine                       # your heroes are here again — no password, no custodian
```

## What just happened (trust model)

Every value-bearing action was either **covenant-enforced** (breeding fairness,
duel settlement, refunds, and marketplace sales — the emulator refuses to co-sign
an invalid shape, e.g. a buyer who underpays the seller) or **client-verifiable**
(commit–reveal fairness audits, signed portable receipts, the receipts-recomputed
leaderboard). The server never held your keys, and never had the authority to mint
a wrong-genome child, settle a duel to the wrong winner, steal a stake, take an
item without paying the ask, or forge progression. That is the whole point.

Optional: set `ARKADE_HEROES_WALLET_PASSPHRASE` before starting a client to
encrypt that wallet's mnemonic at rest (AES-256-GCM); the same passphrase is then
required to reopen it.

Not in this MVP (parking lot): hero sales (same offer primitive, more metadata
care) — see `docs/plans/20-mvp-completion.md`.
