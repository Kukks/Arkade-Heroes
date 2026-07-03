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

    /// <summary>SQLite database file for NArk storage + game chain bookkeeping.</summary>
    public string DbPath { get; set; } = "arkade-heroes-chain.db";

    /// <summary>
    /// Arkade Script emulator (covenant co-signing service). Optional — the
    /// game runs without it in server-enforced mode, but covenant paths
    /// require it.
    /// </summary>
    public string EmulatorUri { get; set; } = "http://localhost:7073";

    /// <summary>
    /// Optional fixed BIP-39 mnemonic for the treasury wallet. When empty a
    /// mnemonic is generated on first boot and persisted in wallet storage.
    /// </summary>
    public string? TreasuryMnemonic { get; set; }
}
