namespace ArkadeHeroes.Chain.NArk;

public class NArkChainOptions
{
    public const string SectionName = "Chain:NArk";

    /// <summary>arkd endpoint (denigiri regtest default).</summary>
    public string ArkUri { get; set; } = "http://localhost:7070";

    /// <summary>
    /// Esplora-compatible REST API. The denigiri stack serves it under
    /// mempool's <c>/api</c> path (unlike older nigiri layouts).
    /// </summary>
    public string EsploraUri { get; set; } = "http://localhost:3000/api";

    /// <summary>
    /// SQLite database file for NArk storage + game chain bookkeeping. This file holds the
    /// TREASURY WALLET's seed, so wherever it points must survive a redeploy — the relative
    /// default lands in the working directory, which inside a container is the ephemeral
    /// writable layer. The container image and docker-compose.yml both override it onto
    /// <c>/data</c>; the default stays relative because it is the right one for
    /// <c>dotnet run</c>, and changing it would move the database out from under any
    /// deployment already using it.
    /// </summary>
    public string DbPath { get; set; } = "arkade-heroes-chain.db";

    /// <summary>
    /// Arkade Script emulator (covenant co-signing service). Optional — the
    /// game runs without it in server-enforced mode, but covenant paths
    /// require it.
    /// </summary>
    public string EmulatorUri { get; set; } = "http://localhost:7073";

    /// <summary>
    /// Optional fixed BIP-39 mnemonic for the treasury wallet. When empty the treasury is
    /// whatever <see cref="DbPath"/> already holds, and a new one is generated only if
    /// <see cref="AllowTreasuryAutoCreate"/> says so. Setting it is what makes the treasury
    /// RECOVERABLE — the seed then exists somewhere other than that one database file.
    /// </summary>
    public string? TreasuryMnemonic { get; set; }

    /// <summary>
    /// Permission to generate a brand-new treasury when none is recorded. OFF by default,
    /// and it has to stay off.
    ///
    /// <para>The server cannot tell a genuine first install from a lost database: both look
    /// like "no treasury row". Answering both by generating a fresh mnemonic silently ROTATES
    /// the treasury to an address nobody recorded and strands every sat at the old one — on
    /// mainnet, real bitcoin — while the server boots clean and reports healthy. So it refuses
    /// instead, and this flag is how an operator says the situation really is a fresh install.</para>
    ///
    /// <para>Turning it on is only safe when there is nothing to lose. To RESTORE a treasury,
    /// leave it off and set <see cref="TreasuryMnemonic"/>, or point <see cref="DbPath"/> at the
    /// database that holds it.</para>
    /// </summary>
    public bool AllowTreasuryAutoCreate { get; set; }
}
