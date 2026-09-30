using Microsoft.Data.Sqlite;

namespace Inventory.Api;

/// <summary>
/// The database.
///
/// Read-write, for one reason: the manual source. Everything a sync owns is written
/// by ADF and only read here; the one exception is what a person says, which is a
/// row of its own rather than an edit of theirs - see <c>ManualWriter</c>.
///
/// WAL so that a write never blocks the reads: the Angular app is reading this file
/// while somebody annotates an entity in it.
/// </summary>
public sealed class Db(string path)
{
    public string Path { get; } = path;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 4000;   -- rather wait than fail on a concurrent write
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -65536;   -- 64 MB page cache
            PRAGMA mmap_size = 268435456; -- 256 MB memory-mapped reads
            """;
        pragmas.ExecuteNonQuery();
        return connection;
    }
}
