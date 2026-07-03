namespace ArkadeHeroes.Server;

public class GameOptions
{
    public const string SectionName = "Game";

    public long BreedingFeeSats { get; set; } = 1_000;

    /// <summary>Base unit for breeding cooldowns. Short by default so regtest play loops stay fast.</summary>
    public TimeSpan BreedingCooldownBaseUnit { get; set; } = TimeSpan.FromSeconds(30);
}
