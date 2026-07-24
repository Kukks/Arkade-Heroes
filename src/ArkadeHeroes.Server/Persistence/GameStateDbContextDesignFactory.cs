using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// Design-time factory used ONLY by the EF Core tooling (`dotnet ef migrations …`). At runtime the app builds
/// the context from configuration (Game:StateDbPath); the CLI has no host, so it needs a stand-in connection
/// to read the model and scaffold a migration. The path here is never opened — migrations are generated from
/// the model, not the database.
/// </summary>
public sealed class GameStateDbContextDesignFactory : IDesignTimeDbContextFactory<GameStateDbContext>
{
    public GameStateDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<GameStateDbContext>()
            .UseSqlite("Data Source=arkade-design-time.db")
            .Options;
        return new GameStateDbContext(options);
    }
}
