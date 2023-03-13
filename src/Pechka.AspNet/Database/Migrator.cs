using System;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using FluentMigrator;
using FluentMigrator.Exceptions;
using FluentMigrator.Runner;
using FluentMigrator.Runner.Announcers;
using FluentMigrator.Runner.Generators.Postgres;
using FluentMigrator.Runner.Generators.SqlServer;
using FluentMigrator.Runner.Initialization;
using FluentMigrator.Runner.Initialization.AssemblyLoader;
using FluentMigrator.Runner.Processors;
using FluentMigrator.Runner.Processors.Postgres;
using FluentMigrator.Runner.Processors.SqlServer;
using Npgsql;

namespace Pechka.AspNet.Database
{
    public static class MigrationRunner
    {
        public static void MigrateDb(string connectionString, Assembly asm, DatabaseType database)
        {
            var announcer = new AnnounerWrapper(new ConsoleAnnouncer());
            var ctx = CreateContext(connectionString, database, announcer, asm);
            ctx.Execute();
            if (announcer.Errors.Length != 0)
                throw new Exception("Failed to migrate: \n" + announcer.Errors);
        }

        private static TaskExecutor CreateContext(string connectionString, DatabaseType database, IAnnouncer announcer,
            Assembly asm)
        {
            var ctx = new RunnerContext(announcer)
            {
                Database = database.ToString(),
                Connection = connectionString,
                Targets = new[] {"self"},
                PreviewOnly = false,
                Namespace = null,
                NestedNamespaces = true,
                Task = "migrate",
                WorkingDirectory = Directory.GetCurrentDirectory()
            };
            return new TaskExecutor(ctx, new LoaderFactory(asm), new ProcessorFactory(connectionString, database));
        }

        private class AnnounerWrapper : IAnnouncer
        {
            private readonly IAnnouncer _relay;

            public string Errors = "";

            public AnnounerWrapper(IAnnouncer relay) => _relay = relay;

            public void Heading(string message) => _relay.Heading(message);

            public void Say(string message) => _relay.Say(message);

            public void Emphasize(string message) => _relay.Emphasize(message);

            public void Sql(string sql) => _relay.Sql(sql);

            public void ElapsedTime(TimeSpan timeSpan) => _relay.ElapsedTime(timeSpan);

            public void Error(string message)
            {
                _relay.Error(message);
                Errors += message + "\n";
            }

            public void Error(Exception exception)
            {
                _relay.Error(exception);
                Errors += exception + "\n";
            }

            public void Write(string message, bool escaped) => _relay.Write(message, escaped);
        }

        private class ProcessorFactory : MigrationProcessorFactoryProvider
        {
            private readonly string _connString;
            private readonly DatabaseType _databaseType;

            public ProcessorFactory(string connString, DatabaseType database)
            {
                _connString = connString;
                _databaseType = database;
            }

            public override IMigrationProcessorFactory GetFactory(string name) =>
                _databaseType switch
                {
                    DatabaseType.Pgsql => new CustomPgsql(_connString),
                    DatabaseType.SqlServer => new CustomSqlServer(_connString),
                    _ => throw new DatabaseOperationNotSupportedException(
                        $"{nameof(ProcessorFactory)} Doesn't support ${_databaseType} database type")
                };

            private class CustomSqlServer : MigrationProcessorFactory
            {
                private readonly string _connString;

                public CustomSqlServer(string connString) => _connString = connString;

                public override IMigrationProcessor Create(string connectionString, IAnnouncer announcer,
                    IMigrationProcessorOptions options)
                {
                    var factory = new SqlServerDbFactory();
                    var connection = new SqlConnection(_connString);
                    return new SqlServerProcessor(connection, new SqlServer2014Generator(), announcer, options,
                        factory);
                }
            }

            private class CustomPgsql : MigrationProcessorFactory
            {
                private readonly string _connString;

                public CustomPgsql(string connString) => _connString = connString;

                public override IMigrationProcessor Create(string connectionString, IAnnouncer announcer,
                    IMigrationProcessorOptions options)
                {
                    var factory = new PostgresDbFactory();
                    var connection = new NpgsqlConnection(_connString);
                    return new PostgresProcessor(connection, new PostgresGenerator(), announcer, options, factory);
                }
            }
        }

        private class LoaderFactory : AssemblyLoaderFactory, IAssemblyLoader
        {
            private readonly Assembly _assembly;

            public LoaderFactory(Assembly assembly) => _assembly = assembly;

            public Assembly Load() => _assembly;

            public override IAssemblyLoader GetAssemblyLoader(string name) => this;
        }
    }
}