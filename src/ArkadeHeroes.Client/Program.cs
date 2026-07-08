using ArkadeHeroes.Client;
using ArkadeHeroes.Client.Sdk;

var serverUrl = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("ARKADE_HEROES_SERVER") ?? "http://localhost:5210";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("""
    ╔══════════════════════════════════════╗
    ║       A R K A D E   H E R O E S      ║
    ║   breed · level · equip · battle     ║
    ║  heroes are Arkade assets on Bitcoin ║
    ╚══════════════════════════════════════╝
    """);
Console.WriteLine($"server: {serverUrl}   (type 'help' for commands)\n");

var game = new GameClient(serverUrl);
await game.TryResumeSessionAsync();

while (true)
{
    Console.Write("heroes> ");
    var line = Console.ReadLine();
    if (line is null) break;
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) continue;

    try
    {
        var done = await game.ExecuteAsync(parts);
        if (done) break;
    }
    catch (GameClientException ex)
    {
        Console.WriteLine($"  ✗ {ex.Message}");
    }
    catch (ArkadeHeroesApiException ex)
    {
        Console.WriteLine($"  ✗ {ex.Message}");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"  ✗ cannot reach server: {ex.Message}");
    }
}

await game.DisposeAsync();
