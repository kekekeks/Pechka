using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.Tests.Infrastructure;

/// <summary>Stand-in for a transient database failure; wired up via PechkaDbTransactionOptions.IsTransientFailure.</summary>
public sealed class FakeTransientException : Exception
{
    public FakeTransientException(string message = "fake transient failure") : base(message)
    {
    }
}

public sealed class FakePermanentException : Exception
{
    public FakePermanentException(string message = "fake permanent failure") : base(message)
    {
    }
}

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

public sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(_entries);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<LogEntry> _entries;

        public ListLogger(List<LogEntry> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ListLoggerFactory _factory;
    private readonly ILogger _inner;

    public ListLogger(ListLoggerFactory factory)
    {
        _factory = factory;
        _inner = factory.CreateLogger(typeof(T).Name);
    }

    public IReadOnlyList<LogEntry> Entries => _factory.Entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}

public sealed class FakeApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => _stopping.Cancel();
}

public static class Poll
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Waits for a condition instead of sleeping a fixed amount, so tests stay fast and stable.</summary>
    public static async Task Until(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(5);
        }
    }
}
