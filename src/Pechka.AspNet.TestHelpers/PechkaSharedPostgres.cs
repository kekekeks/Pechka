using System.Data.Common;
using Testcontainers.PostgreSql;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// The one PostgreSQL container for the whole test process. Every database the suite uses — the
/// pooled lanes and each isolated host — is a <c>CREATE DATABASE</c> on this server, because a
/// database costs ~0.1s while a container costs ~2s and ~100MB.
///
/// <para>Deliberately owned by no fixture (fixture lifetimes are xunit-version specific): the
/// container lives until the test process exits, with Testcontainers' Ryuk reaper cleaning it up
/// afterwards. A suite that wants an eager, pool-attributed startup failure can call
/// <see cref="StartedAsync"/> from its own assembly-level fixture.</para>
/// </summary>
public static class PechkaSharedPostgres
{
    // Guards first-use creation of the container, nothing else.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? _container;

    /// <summary>Backends the server accepts. One server backs every lane and isolated host at
    /// once, and a host is not one client — Npgsql pools per data source — so the stock 100 is
    /// exhausted within seconds by a lane pool; the symptom would be a connect failure in an
    /// unrelated test. Bounded on the client side by
    /// <see cref="PechkaTestApp.AmendConnectionString"/>.</summary>
    private const int MaxConnections = 500;

    public static async Task<PostgreSqlContainer> StartedAsync(PechkaTestApp app)
    {
        if (_container is { } already)
            return already;
        await Gate.WaitAsync();
        try
        {
            if (_container == null)
            {
                var container = new PostgreSqlBuilder()
                    .WithImage(app.PostgresImage)
                    .WithCommand("-c", $"max_connections={MaxConnections}")
                    .Build();
                await container.StartAsync();
                _container = container;
            }
        }
        finally
        {
            Gate.Release();
        }
        return _container;
    }

    /// <summary>A fresh, empty database on the shared server and the connection string reaching
    /// it. Migrations run when a host starts on it, so the caller begins from a virgin schema.</summary>
    public static async Task<string> CreateDatabaseAsync(PechkaTestApp app, string name)
    {
        var container = await StartedAsync(app);
        var created = await container.ExecScriptAsync($"CREATE DATABASE \"{name}\";");
        if (created.ExitCode != 0 || created.Stderr.Contains("ERROR"))
            throw new InvalidOperationException(
                $"Could not create the database {name}: {created.Stderr}{created.Stdout}");

        // Same server, different database — the connection string differs only in that key.
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = container.GetConnectionString()
        };
        connection["Database"] = name;
        return app.AmendConnectionString(connection.ConnectionString);
    }
}
