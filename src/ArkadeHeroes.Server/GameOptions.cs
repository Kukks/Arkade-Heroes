namespace ArkadeHeroes.Server;

public class GameOptions
{
    public const string SectionName = "Game";

    public long BreedingFeeSats { get; set; } = 1_000;

    /// <summary>Base unit for breeding cooldowns. Short by default so regtest play loops stay fast.</summary>
    public TimeSpan BreedingCooldownBaseUnit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fixed 32-byte hex key for signing progression receipts; ephemeral per process when unset.</summary>
    public string? ReceiptKeyHex { get; set; }

    /// <summary>How long after a covenant match opens its escrow refund leaves unlock (player liveness).</summary>
    public TimeSpan WagerEscrowRefundAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Mirror earned XP on-chain as a fungible XP-asset delivery per resolved
    /// match. OFF by default: on-chain delivery is a treasury spend, and the
    /// signed receipts already carry XP portably — enable it where the on-chain
    /// balance is wanted (the walkthrough) and the extra treasury load is
    /// acceptable. In-memory play always mirrors it (no treasury cost).
    /// </summary>
    public bool DeliverXpAssetsOnChain { get; set; } = false;
}
