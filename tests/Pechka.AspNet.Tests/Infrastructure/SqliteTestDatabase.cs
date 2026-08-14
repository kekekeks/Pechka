using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests.Infrastructure;

[Table("TestItems")]
public class TestItem
{
    [PrimaryKey, Identity]
    public int Id { get; set; }

    [Column, NotNull]
    public string Name { get; set; } = null!;
}

public class TestDbContext : DataConnection
{
    public TestDbContext(string connectionString)
        : base(new DataOptions().UseConnectionString(ProviderName.SQLiteMS, connectionString))
    {
    }
}

public class TestDbManager : TransactionalDbContextManagerBase<TestDbContext>
{
    public TestDbManager(string connectionString) : base(() => new TestDbContext(connectionString))
    {
    }
}

/// <summary>
/// A temp-file SQLite database shared by all tests of one class. A file (not an in-memory) database
/// is required because the managers open several independent connections that must see one database.
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly string _path;

    public SqliteTestDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pechka-tests-{Guid.NewGuid():N}.db");
        ConnectionString = $"Data Source={_path};Default Timeout=30;Cache=Private";
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        // WAL keeps readers from being blocked by an open writer transaction, which several
        // tests rely on when inspecting the database from outside an uncommitted scope.
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, """
            CREATE TABLE TestItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL)
            """);
        Execute(connection, """
            CREATE TABLE BackgroundJobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Type TEXT NOT NULL,
                Payload TEXT NULL,
                State INTEGER NOT NULL,
                Attempts INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                TakenAt TEXT NULL,
                FinishedAt TEXT NULL,
                Error TEXT NULL)
            """);
        Execute(connection, "CREATE INDEX IX_BackgroundJobs_State_Id ON BackgroundJobs (State, Id)");
    }

    public string ConnectionString { get; }

    public TestDbManager CreateManager() => new(ConnectionString);

    public TestDbContext CreateContext() => new(ConnectionString);

    /// <summary>Wipes all tables; called from test constructors so tests of a class don't leak into each other.</summary>
    public void Reset()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        Execute(connection, "DELETE FROM TestItems");
        Execute(connection, "DELETE FROM BackgroundJobs");
        Execute(connection, "DELETE FROM sqlite_sequence");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A pooled connection may still hold the file on some platforms; temp files are disposable
            }
    }
}

/// <summary>Convenience base for test classes owning a <see cref="SqliteTestDatabase"/> fixture.</summary>
public abstract class SqliteTestBase : IClassFixture<SqliteTestDatabase>
{
    protected SqliteTestBase(SqliteTestDatabase db)
    {
        Db = db;
        db.Reset();
    }

    protected SqliteTestDatabase Db { get; }

    protected async Task<string[]> ReadItemNames()
    {
        await using var ctx = Db.CreateContext();
        return await ctx.GetTable<TestItem>().OrderBy(x => x.Id).Select(x => x.Name).ToArrayAsync();
    }

    protected Task<int> CountItems() => CountItemsAsync();

    private async Task<int> CountItemsAsync()
    {
        await using var ctx = Db.CreateContext();
        return await ctx.GetTable<TestItem>().CountAsync();
    }

    protected static Task Insert(TestDbManager manager, string name) =>
        manager.ExecAsync(ctx => ctx.InsertAsync(new TestItem { Name = name }));
}
