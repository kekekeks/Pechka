using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
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
        public static void MigrateDb(string connectionString, Assembly asm, DatabaseType database,
            IReadOnlyList<Assembly>? extraAssemblies = null, IReadOnlyList<Type>? allowedExtraMigrations = null)
        {
            var announcer = new AnnounerWrapper(new ConsoleAnnouncer());
            var ctx = CreateContext(connectionString, database, announcer, asm, extraAssemblies,
                allowedExtraMigrations);
            ctx.Execute();
            if (announcer.Errors.Length != 0)
                throw new Exception("Failed to migrate: \n" + announcer.Errors);
        }

        private static TaskExecutor CreateContext(string connectionString, DatabaseType database, IAnnouncer announcer,
            Assembly asm, IReadOnlyList<Assembly>? extraAssemblies, IReadOnlyList<Type>? allowedExtraMigrations)
        {
            var ctx = new RunnerContext(announcer)
            {
                Database = database.ToString(),
                Connection = connectionString,
                Targets = new[] {"self"},
                TargetAssemblies = extraAssemblies?.ToArray() ?? Array.Empty<Assembly>(),
                PreviewOnly = false,
                Namespace = null,
                NestedNamespaces = true,
                Task = "migrate",
                WorkingDirectory = Directory.GetCurrentDirectory()
            };
            return new CustomTaskExecutor(ctx, new LoaderFactory(asm), new ProcessorFactory(connectionString, database),
                asm, allowedExtraMigrations ?? Array.Empty<Type>());
        }

        private class CustomTaskExecutor : TaskExecutor
        {
            private readonly Assembly _rootAssembly;
            private readonly IReadOnlyList<Type> _allowedExtraMigrations;

            public CustomTaskExecutor(RunnerContext ctx, AssemblyLoaderFactory loaderFactory,
                MigrationProcessorFactoryProvider processorFactory, Assembly rootAssembly,
                IReadOnlyList<Type> allowedExtraMigrations)
                : base(ctx, loaderFactory, processorFactory)
            {
                _rootAssembly = rootAssembly;
                _allowedExtraMigrations = allowedExtraMigrations;
            }

            protected override void Initialize()
            {
                base.Initialize();
                // Migrations outside the root assembly run only when explicitly allowed, so
                // optional framework features can ship migrations that stay inert until enabled
                var conventions = (MigrationConventions)((FluentMigrator.Runner.MigrationRunner)Runner).Conventions;
                var defaultHook = conventions.TypeIsMigration;
                conventions.TypeIsMigration = t =>
                    defaultHook(t) && (t.Assembly == _rootAssembly || _allowedExtraMigrations.Contains(t));
            }
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