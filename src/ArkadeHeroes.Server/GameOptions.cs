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
}
