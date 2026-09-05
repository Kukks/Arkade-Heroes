using ArkadeHeroes.Chain.NArk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Who the treasury belongs to after a restart.
///
/// <para>The treasury wallet's seed lives in the chain database and nowhere else, and the server
/// finds it by reading one key/value row. When that row is absent the server used to generate a
/// fresh BIP-39 mnemonic and carry on — which is right exactly once, on a deliberate first install,
/// and catastrophic every other time. A chain database that was lost (an ephemeral container layer,
/// an unmounted volume, a wrong <c>Chain__NArk__DbPath</c>) is byte-for-byte indistinguishable from
/// a first install, so the server cannot tell "nothing here yet" from "everything is gone" — it
/// answered both by minting a new key, rotating the treasury to an address nobody had recorded and
/// stranding every sat at the old one. On mainnet those sats are real bitcoin.</para>
///
/// <para>So the generate branch is opt-in now, and every other path refuses. These tests hold the
/// refusal in place: no treasury recorded and nothing said about it means STOP, and — the other
/// half, or the gate is just an outage — an operator who has said what they want gets through.</para>
/// </summary>
public class TreasuryProvisioningTests : IDisposable
{
    private readonly List<string> _dbPaths = [];

    public void Dispose()
    {
        foreach (var p in _dbPaths)
        {
            SqliteTestDb.ReleasePool(p);
            try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { /* windows lock */ }
        }
    }

    /// <summary>
    /// The whole defect, in one assertion: an empty chain database plus no configured mnemonic must
    /// stop the server, not hand it a brand-new treasury.
    ///
    /// <para>The refusal has to happen BEFORE any wallet is created or persisted, so the check sits
    /// ahead of the first network call in the creation branch — which is also what lets this test run
    /// without arkd. Reaching the transport at all means the server was already on its way to minting
    /// a key, so a non-<see cref="InvalidOperationException"/> here is the bug reproducing.</para>
    /// </summary>
    [Fact]
    public async Task WithNoTreasuryRecorded_AndNoMnemonic_TheServerRefusesInsteadOfMintingANewOne()
    {
        var (service, db) = await OnEmptyChainDbAsync(new NArkChainOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetInfoAsync());

        // Names both ways out — restore the treasury you have, or say this really is a fresh install.
        // A refusal that only says "no treasury" leaves the operator to guess which one they are in.
        Assert.Contains("Chain__NArk__TreasuryMnemonic", ex.Message);
        Assert.Contains("Chain__NArk__AllowTreasuryAutoCreate", ex.Message);
        // And names the database it actually looked in, since a wrong path is the likeliest cause.
        Assert.Contains(new NArkChainOptions().DbPath, ex.Message);

        // Nothing was written. A refusal that still persisted a wallet id would have rotated the
        // treasury anyway and merely complained about it.
        await using var ctx = await db.CreateDbContextAsync();
        Assert.Empty(await ctx.ChainKv.ToListAsync());
    }

    /// <summary>
    /// A configured mnemonic is the operator saying "this treasury is mine and I hold its seed", so
    /// it needs no second permission — and it is the recoverable case by construction: the key exists
    /// somewhere other than a container volume.
    /// </summary>
    [Fact]
    public async Task WithATreasuryMnemonicConfigured_TheServerProceeds()
    {
        var (service, _) = await OnEmptyChainDbAsync(new NArkChainOptions
        {
            TreasuryMnemonic =
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
        });

        await AssertReachedWalletCreationAsync(service);
    }

    /// <summary>
    /// The deliberate first install: an operator who has explicitly asked for a new treasury gets one.
    /// Without this the gate would be an outage rather than a safety catch — a genuinely new
    /// deployment could never start.
    /// </summary>
    [Fact]
    public async Task WithAutoCreateExplicitlyAllowed_TheServerProceeds()
    {
        var (service, _) = await OnEmptyChainDbAsync(new NArkChainOptions { AllowTreasuryAutoCreate = true });

        await AssertReachedWalletCreationAsync(service);
    }

    /// <summary>
    /// Opt-in is OFF unless someone turns it on. A default of "generate" is the defect itself, and it
    /// is the kind of default that gets restored by accident, so it is pinned.
    /// </summary>
    [Fact]
    public void AutoCreateIsOffByDefault() =>
        Assert.False(new NArkChainOptions().AllowTreasuryAutoCreate);

    /// <summary>
    /// The shipped IMAGE puts the treasury's database on the volume, not in the container.
    ///
    /// <para>The app's own default is the RELATIVE <c>arkade-heroes-chain.db</c>, which resolves against
    /// the image's <c>/app</c> working directory — the writable layer, destroyed whenever a container is
    /// recreated, which is what a redeploy does. docker-compose.yml has always pointed it into the
    /// volume, but a platform deploying the published image directly never reads that file, so the same
    /// server that keeps its treasury under compose loses it under a direct deploy. Same gap, same fix
    /// and same test shape as <c>Game__StateDbPath</c>.</para>
    ///
    /// <para>The relative default is deliberately LEFT ALONE in the app: it is right for
    /// <c>dotnet run</c>, and changing it would move the database out from under any deployment already
    /// using it.</para>
    /// </summary>
    [Fact]
    public void TheShippedImage_DefaultsTheChainDatabaseOntoItsVolume()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "src", "ArkadeHeroes.Server", "Dockerfile"));

        var env = System.Text.RegularExpressions.Regex.Match(
            dockerfile, @"^ENV\s+Chain__NArk__DbPath=(?<path>\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(env.Success,
            "the server image must default Chain__NArk__DbPath — without it a deployment that does not "
            + "use docker-compose.yml keeps the treasury wallet in the container's writable layer, and a "
            + "redeploy destroys the only copy of its seed.");
        Assert.StartsWith("/data/", env.Groups["path"].Value);

        // And compose still points it at the NAMED volume. The image's own VOLUME is anonymous: it
        // survives a restart but is orphaned when the container is recreated.
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        Assert.Contains("- arkade-state:/data", compose);
        Assert.Contains("Chain__NArk__DbPath: /data/", compose);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArkadeHeroes.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Could not locate ArkadeHeroes.slnx above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Past the gate, wallet creation's first act is a network call to arkd, which this test rig has
    /// no transport for. That failure — anything other than the gate's own refusal — is the proof the
    /// server was allowed to continue.
    /// </summary>
    private static async Task AssertReachedWalletCreationAsync(NArkChainService service)
    {
        var ex = await Record.ExceptionAsync(() => service.GetInfoAsync());
        Assert.NotNull(ex);
        Assert.IsNotType<InvalidOperationException>(ex);
    }

    /// <summary>
    /// The service over a real, freshly-created, EMPTY chain database — the exact state a lost volume
    /// leaves behind. Only the database and the options are needed to reach the decision; every other
    /// dependency belongs to work that happens after it, and passing them as null is what proves the
    /// refusal costs no arkd, no emulator and no network.
    /// </summary>
    private async Task<(NArkChainService Service, IDbContextFactory<GameArkDbContext> Db)> OnEmptyChainDbAsync(
        NArkChainOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ah-treasury-gate-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);

        var provider = new ServiceCollection()
            .AddDbContextFactory<GameArkDbContext>(b => b.UseSqlite($"Data Source={path}"))
            .BuildServiceProvider();
        var dbFactory = provider.GetRequiredService<IDbContextFactory<GameArkDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();

        var service = new NArkChainService(
            walletStorage: null!, vtxoStorage: null!, contractStorage: null!, contractService: null!,
            assetManager: null!, spendingService: null!, transport: null!, vtxoSync: null!,
            safetyService: null!, walletProvider: null!, intentStorage: null!,
            dbFactory: dbFactory, options: options,
            logger: NullLogger<NArkChainService>.Instance);

        return (service, dbFactory);
    }
}
