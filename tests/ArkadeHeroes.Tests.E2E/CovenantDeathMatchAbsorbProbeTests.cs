using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Fairness;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the ABSORB death-match settle, live on regtest. Unlike the classic
/// settle (winner's hero passes through), an absorb-mint settle BURNS BOTH staked heroes and
/// MINTS a new absorbed hero under the species to the winner — the merge shape, fee-less,
/// winner-gated by a DISTINCT oracle message. This builds the settleMint leaf directly (a
/// funder-issued species + a test oracle) so it can craft malicious packets and prove the
/// emulator REFUSES each cheat: an un-attested genome (root CSFS), the wrong-winner branch,
/// forcing a mint without the mint-message sig, keeping either "burned" hero alive, and paying
/// the minted hero to the wrong script. The honest absorb-mint co-signs. Proves the absorb
/// settle is packet-trustless before any wiring (T1 pivot gate).
/// </summary>
public class CovenantDeathMatchAbsorbProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _challenger = null!;
    private SelfCustodyWallet _defender = null!; // supplies a second party's script + a theft destination
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _challenger = await NewWalletAsync();
        _defender = await NewWalletAsync();
        await RegtestHelper.ArkSend(_challenger.Address, 300_000);
        await _challenger.WaitForBalanceAsync(300_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-absorbprobe-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    public async Task DisposeAsync()
    {
        await _challenger.DisposeAsync();
        await _defender.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private static (byte[] Priv, byte[] Pub) NewOracle()
    {
        var key = NBitcoin.Secp256k1.ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> pk = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pk);
        var priv = new byte[32];
        key.WriteToSpan(priv);
        return (priv, pk.ToArray());
    }

    private async Task<string> IssueAsync(ulong amount)
    {
        var mgr = _challenger.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_challenger.WalletId, new IssuanceParams(Amount: amount));
        await _challenger.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        return res.AssetId;
    }

    private static byte[] Sign(byte[] oraclePriv, byte[] message32)
    {
        var sig = new byte[64];
        NBitcoin.Secp256k1.ECPrivKey.Create(oraclePriv).SignBIP340(message32).WriteToSpan(sig);
        return sig;
    }

    [Fact]
    public async Task AbsorbMintSettle_BurnsBothHeroes_MintsAbsorbedUnderSpecies_ToWinner()
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;

        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));
        var (oraclePriv, oraclePk) = NewOracle();
        const string matchId = "e2e-absorb-mint";

        // A species control asset (existence-only) + two staked heroes (winner + loser).
        var speciesId = await IssueAsync(1);
        var winnerHeroId = await IssueAsync(1);
        var loserHeroId = await IssueAsync(1);
        var species = AssetId.FromString(speciesId);
        var winnerHero = AssetId.FromString(winnerHeroId);
        var loserHero = AssetId.FromString(loserHeroId);
        var chalScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;

        // Build a contract carrying JUST the settleMint leaf (challenger = winner).
        var settleLeaf = DeathMatchEscrowContracts.SettleMintLeaf(
            winnerHero, loserHero, chalScript, species, oraclePk, commitment, matchId,
            challengerWon: true, mergedGear: []);
        var contract = new ArkadeArtifactContract(
            "absorb-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey, [new("settleMint", settleLeaf)]);
        var addr = contract.GetArkAddress().ToString(isMain);

        // Stake both heroes: winner first (→ input 0), loser second (→ input 1).
        await _challenger.SendAssetAsync(addr, winnerHeroId, 1);
        await _challenger.SendAssetAsync(addr, loserHeroId, 1);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_challenger, contract, 2, TimeSpan.FromSeconds(60));
        var winnerVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == winnerHeroId) == true);
        var loserVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == loserHeroId) == true);
        var ordered = new[] { winnerVtxo, loserVtxo };
        var total = ordered.Sum(v => (long)v.Amount);

        // The absorbed hero's metadata + the oracle's TWO sigs: the absorb-mint OUTCOME (winner)
        // and the minted metadata ROOT (correct genome). Dummy genome — the covenant only binds
        // the root the oracle signed + the mint under species at output 0.
        var meta = BreedEscrowContracts.ChildMetadata(
            new string('a', 64), 1, winnerHeroId, loserHeroId, new string('b', 64), "absorb-probe");
        var root = ArkadeCovenants.MetadataMerkleRoot(meta);
        var sigOutcome = Sign(oraclePriv, ArkadeCovenants.DeathMatchAbsorbMintMessage(matchId, challengerWon: true));
        var sigRoot = Sign(oraclePriv, root);

        var honestWitness = ArkadeCovenants.DeathMatchAbsorbMintWitness(
            sigRoot, childGroupIndex: 2, loserInputIndex: 1, winnerInputIndex: 0, serverSeed, sigOutcome);
        var dust = serverInfo.Dust.Satoshi;
        var defScript = ArkAddress.Parse(_defender.Address).ScriptPubKey;

        AssetGroup BurnWinner() => AssetGroup.Create(winnerHero, null, [AssetInput.Create(0, 1)], [], []);
        AssetGroup BurnLoser() => AssetGroup.Create(loserHero, null, [AssetInput.Create(1, 1)], [], []);
        AssetGroup Mint(ushort vout) => AssetGroup.Create(null, AssetRef.FromId(species), [], [AssetOutput.Create(vout, 1)], meta);
        CovenantSpender.CovenantInput[] Inputs(byte[][] w) =>
            ordered.Select(v => new CovenantSpender.CovenantInput(contract, "settleMint", w, v)).ToArray();

        // ── Cheat: un-attested genome — mint a DIFFERENT genome (the oracle only signed `meta`'s
        //    root) → the minted root ≠ the signed root → the root CSFS refuses.
        var meta2 = BreedEscrowContracts.ChildMetadata(
            new string('c', 64), 1, winnerHeroId, loserHeroId, new string('d', 64), "cheat");
        var badGenome = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(honestWitness),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create([BurnWinner(), BurnLoser(),
                AssetGroup.Create(null, AssetRef.FromId(species), [], [AssetOutput.Create(0, 1)], meta2)])]));
        Assert.Contains("Emulator rejected", badGenome.Message);

        // ── Cheat: wrong-winner — the oracle signs the DEFENDER-win outcome, but this leaf bakes the
        //    challenger-win message → the outcome CSFS refuses.
        var wrongWinner = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri,
            Inputs(ArkadeCovenants.DeathMatchAbsorbMintWitness(sigRoot, 2, 1, 0, serverSeed,
                Sign(oraclePriv, ArkadeCovenants.DeathMatchAbsorbMintMessage(matchId, challengerWon: false)))),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create([BurnWinner(), BurnLoser(), Mint(0)])]));
        Assert.Contains("Emulator rejected", wrongWinner.Message);

        // ── Cheat: force a mint with a KEEP signature — the oracle signed the passthrough/keep
        //    message (DeathMatchSettleMessage), not the absorb-mint message → the mint leaf refuses.
        var forceMint = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri,
            Inputs(ArkadeCovenants.DeathMatchAbsorbMintWitness(sigRoot, 2, 1, 0, serverSeed,
                Sign(oraclePriv, ArkadeCovenants.DeathMatchSettleMessage(matchId, challengerWon: true)))),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create([BurnWinner(), BurnLoser(), Mint(0)])]));
        Assert.Contains("Emulator rejected", forceMint.Message);

        // ── Cheat: keep the LOSER hero alive — route it to output 1 instead of burning → AssetBurned refuses.
        var loserSurvives = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(honestWitness),
            [new TxOut(Money.Satoshis(total - dust), chalScript), new TxOut(Money.Satoshis(dust), defScript)],
            extraPackets: [Packet.Create([
                BurnWinner(),
                AssetGroup.Create(loserHero, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
                Mint(0)])]));
        Assert.Contains("Emulator rejected", loserSurvives.Message);

        // ── Cheat: keep the OLD WINNER hero alive — route it to output 1 → AssetBurned refuses.
        var winnerSurvives = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(honestWitness),
            [new TxOut(Money.Satoshis(total - dust), chalScript), new TxOut(Money.Satoshis(dust), defScript)],
            extraPackets: [Packet.Create([
                AssetGroup.Create(winnerHero, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(1, 1)], []),
                BurnLoser(), Mint(0)])]));
        Assert.Contains("Emulator rejected", winnerSurvives.Message);

        // ── Cheat: mint the absorbed hero to the WRONG script — output 0 pays the defender → MintToPlayer refuses.
        var wrongScript = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(honestWitness),
            [new TxOut(Money.Satoshis(total), defScript)],
            extraPackets: [Packet.Create([BurnWinner(), BurnLoser(), Mint(0)])]));
        Assert.Contains("Emulator rejected", wrongScript.Message);

        // ── Honest absorb-mint: burn BOTH heroes, mint the absorbed hero (group 2) under species at
        //    output 0 → the winner. Co-signed (submit once).
        var response = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(honestWitness),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create([BurnWinner(), BurnLoser(), Mint(0)])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx), "the honest absorb-mint settle must be emulator-co-signed");
    }
}
