using System.Data;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Tests;

internal sealed class RecordingManager : ITransactionalDbContextManager
{
    private readonly List<string> _log;
    private readonly string _name;
    private readonly bool _failOnBegin;

    public RecordingManager(string name, List<string> log, bool failOnBegin = false)
    {
        _name = name;
        _log = log;
        _failOnBegin = failOnBegin;
    }

    public RecordingScope? Scope { get; private set; }
    public IsolationLevel RequestedIsolationLevel { get; private set; }

    public IDbContextTransactionScope BeginTransaction(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
    {
        if (_failOnBegin)
            throw new FakePermanentException($"{_name} cannot begin");
        RequestedIsolationLevel = isolationLevel;
        _log.Add($"begin:{_name}");
        return Scope = new RecordingScope(_name, _log);
    }

    public IDbContextTransactionScope? CurrentTransaction => Scope;
}

internal sealed class RecordingScope : IDbContextTransactionScope
{
    private readonly List<string> _log;
    private readonly string _name;

    public RecordingScope(string name, List<string> log)
    {
        _name = name;
        _log = log;
    }

    public bool IsCompleted { get; private set; }
    public bool IsRollbackOnly { get; private set; }
    public bool IsTransactionStarted { get; set; }

    public Task CommitAsync(CancellationToken token = default)
    {
        _log.Add($"commit:{_name}");
        IsCompleted = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken token = default)
    {
        _log.Add($"rollback:{_name}");
        IsCompleted = true;
        return Task.CompletedTask;
    }

    public void SetRollbackOnly() => IsRollbackOnly = true;

    public void OnCommitted(Action callback)
    {
    }

    public void Dispose() => _log.Add($"dispose:{_name}");

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

public class TransactionScopeSetTests : SqliteTestBase
{
    public TransactionScopeSetTests(SqliteTestDatabase db) : base(db)
    {
    }

    private static PechkaDbTransactionOptions Options(TimeSpan? warningThreshold = null) => new()
    {
        IsolationLevel = IsolationLevel.Serializable,
        LongTransactionWarningThreshold = warningThreshold ?? TimeSpan.FromMinutes(5)
    };

    [Fact]
    public async Task Scopes_Are_Begun_In_Registration_Order_With_The_Configured_Isolation_Level()
    {
        var log = new List<string>();
        var managers = new[] { new RecordingManager("a", log), new RecordingManager("b", log) };
        var options = Options();

        await using (TransactionScopeSet.Begin(managers, options, new ListLoggerFactory().CreateLogger("t"), "op"))
        {
        }

        Assert.Equal("begin:a", log[0]);
        Assert.Equal("begin:b", log[1]);
        Assert.All(managers, m => Assert.Equal(IsolationLevel.Serializable, m.RequestedIsolationLevel));
    }

    [Fact]
    public async Task Commit_Runs_In_Registration_Order_And_Dispose_In_Reverse()
    {
        var log = new List<string>();
        var managers = new[]
        {
            new RecordingManager("a", log), new RecordingManager("b", log), new RecordingManager("c", log)
        };

        var set = TransactionScopeSet.Begin(managers, Options(), new ListLoggerFactory().CreateLogger("t"), "op");
        await set.CommitAsync();
        await set.DisposeAsync();

        Assert.Equal(new[]
        {
            "begin:a", "begin:b", "begin:c",
            "commit:a", "commit:b", "commit:c",
            "dispose:c", "dispose:b", "dispose:a"
        }, log);
    }

    [Fact]
    public void A_Failing_Begin_Disposes_The_Scopes_Created_So_Far()
    {
        var log = new List<string>();
        var managers = new ITransactionalDbContextManager[]
        {
            new RecordingManager("a", log), new RecordingManager("b", log, failOnBegin: true)
        };

        Assert.Throws<FakePermanentException>(() => TransactionScopeSet.Begin(managers, Options(),
            new ListLoggerFactory().CreateLogger("t"), "op"));

        Assert.Equal(new[] { "begin:a", "dispose:a" }, log);
    }

    [Fact]
    public async Task Untouched_Managers_Open_Nothing()
    {
        await using var first = Db.CreateManager();
        await using var second = Db.CreateManager();
        var managers = new ITransactionalDbContextManager[] { first, second };

        await using (TransactionScopeSet.Begin(managers, Options(), new ListLoggerFactory().CreateLogger("t"), "op"))
        {
            Assert.False(first.CurrentTransaction!.IsTransactionStarted);
            Assert.False(second.CurrentTransaction!.IsTransactionStarted);
        }

        Assert.Null(first.CurrentTransaction);
        Assert.Null(second.CurrentTransaction);
    }

    [Fact]
    public async Task Commit_Persists_The_Work_Of_A_Real_Manager()
    {
        // SQLite allows a single writer, so only one of the set's managers writes here
        await using var writer = Db.CreateManager();
        await using var idle = Db.CreateManager();
        var managers = new ITransactionalDbContextManager[] { writer, idle };

        var set = TransactionScopeSet.Begin(managers, Options(), new ListLoggerFactory().CreateLogger("t"), "op");
        await Insert(writer, "a");
        Assert.Empty(await ReadItemNames());
        await set.CommitAsync();
        await set.DisposeAsync();

        Assert.Equal(new[] { "a" }, await ReadItemNames());
    }

    [Fact]
    public async Task Dispose_Without_Commit_Rolls_Everything_Back()
    {
        await using var writer = Db.CreateManager();
        var managers = new ITransactionalDbContextManager[] { writer };

        await using (TransactionScopeSet.Begin(managers, Options(), new ListLoggerFactory().CreateLogger("t"), "op"))
            await Insert(writer, "a");

        Assert.Empty(await ReadItemNames());
    }

    [Fact]
    public async Task A_Long_Held_Started_Transaction_Logs_A_Warning()
    {
        var log = new List<string>();
        var loggerFactory = new ListLoggerFactory();
        var manager = new RecordingManager("a", log);

        var set = TransactionScopeSet.Begin(new[] { manager }, Options(TimeSpan.Zero),
            loggerFactory.CreateLogger("t"), "slow op");
        manager.Scope!.IsTransactionStarted = true;
        await set.DisposeAsync();

        Assert.Contains(loggerFactory.Entries,
            e => e.Message.Contains("Database transaction for slow op was held for"));
    }

    [Fact]
    public async Task No_Warning_When_No_Transaction_Was_Ever_Started()
    {
        var log = new List<string>();
        var loggerFactory = new ListLoggerFactory();
        var manager = new RecordingManager("a", log);

        await using (TransactionScopeSet.Begin(new[] { manager }, Options(TimeSpan.Zero),
                         loggerFactory.CreateLogger("t"), "op"))
        {
        }

        Assert.Empty(loggerFactory.Entries);
    }

    [Fact]
    public async Task A_Started_Transaction_Is_Still_Reported_After_A_Commit()
    {
        var log = new List<string>();
        var loggerFactory = new ListLoggerFactory();
        var manager = new RecordingManager("a", log);

        var set = TransactionScopeSet.Begin(new[] { manager }, Options(TimeSpan.Zero),
            loggerFactory.CreateLogger("t"), "op");
        manager.Scope!.IsTransactionStarted = true;
        await set.CommitAsync();
        // CommitAsync leaves the scope completed, so the flag must have been latched at commit time
        manager.Scope.IsTransactionStarted = false;
        await set.DisposeAsync();

        Assert.Contains(loggerFactory.Entries, e => e.Message.Contains("Database transaction for op was held for"));
    }
}
