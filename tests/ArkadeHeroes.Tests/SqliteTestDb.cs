namespace ArkadeHeroes.Tests;

internal static class SqliteTestDb
{
    /// <summary>Releases the OS handle on ONE test's state database so the file can be deleted.
    /// <c>ClearAllPools</c> did this process-wide, which also disposed the pooled connections of the
    /// SQLite-backed hosts other classes run in parallel — those then threw
    /// <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> out of whatever request was mid-flight.</summary>
    public static void ReleasePool(string dbPath)
    {
        using var handle = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(handle);
    }
}
