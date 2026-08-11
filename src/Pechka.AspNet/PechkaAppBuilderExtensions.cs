using System;
using System.Data;
using System.Linq;
using CoreRPC;
using LinqToDB.Data;
using LinqToDB.DataProvider;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.DataProvider.SqlServer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pechka.AspNet.Database;

namespace Pechka.AspNet;
public static class PechkaAppBuilderExtensions
{
    class DbConfigOptions<TContextManager> : DatabaseConfig
    {
        
    }

    public static IServiceCollection AddConfigSection<TConfig>(this IServiceCollection services, string section) where TConfig : class
    {
        return services.AddSingleton<TConfig>(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection(section).Get<TConfig>());
    }
    
    public static IServiceCollection AddDbContextManager<TContextManager>(this IServiceCollection services,
        Func<IDataProvider, string, TContextManager> factory, string configSection = "Database", bool runMigrations = true)
        where TContextManager : class
        => services.AddDbContextManagerCore(factory, configSection, runMigrations, ServiceLifetime.Singleton);

    /// <summary>
    /// Registers a scoped, unit-of-work capable context manager (see
    /// <see cref="TransactionalDbContextManagerBase{TContext}"/>) and, once per app, the implicit
    /// transaction entry points: a CoreRPC interceptor and an MVC action filter that wrap each
    /// call/action in a lazy transaction scope (commit on success, rollback on exception, opt-out
    /// via [NoTransaction]). May be called multiple times for multiple databases; scopes are then
    /// committed sequentially per manager, without cross-database atomicity.
    /// </summary>
    public static IServiceCollection AddTransactionalDbContextManager<TContextManager>(this IServiceCollection services,
        Func<IDataProvider, string, TContextManager> factory, string configSection = "Database",
        bool runMigrations = true, Action<PechkaDbTransactionOptions>? configure = null)
        where TContextManager : class, ITransactionalDbContextManager
    {
        services.AddDbContextManagerCore(factory, configSection, runMigrations, ServiceLifetime.Scoped);
        services.AddScoped<ITransactionalDbContextManager>(sp => sp.GetRequiredService<TContextManager>());

        // The options singleton doubles as the "adapters already registered" marker
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PechkaDbTransactionOptions));
        PechkaDbTransactionOptions options;
        if (existing == null)
        {
            options = new PechkaDbTransactionOptions();
            services.AddSingleton(options);
            services.AddSingleton<IMethodCallInterceptor, TransactionMethodCallInterceptor>();
            services.Configure<MvcOptions>(o => o.Filters.Add(typeof(TransactionActionFilter)));
        }
        else
            options = (PechkaDbTransactionOptions)existing.ImplementationInstance!;
        configure?.Invoke(options);
        return services;
    }

    private static IServiceCollection AddDbContextManagerCore<TContextManager>(this IServiceCollection services,
        Func<IDataProvider, string, TContextManager> factory, string configSection, bool runMigrations,
        ServiceLifetime lifetime)
        where TContextManager : class
    {
        services.AddConfigSection<DbConfigOptions<TContextManager>>(configSection);
        services.Add(new ServiceDescriptor(typeof(TContextManager), sp =>
        {
            var opts = sp.GetRequiredService<DbConfigOptions<TContextManager>>();
            if (opts.Type == DatabaseType.SqlServer)
                return factory(SqlServerTools.GetDataProvider(connectionString: opts.ConnectionString),
                    opts.ConnectionString);
            return factory(PostgreSQLTools.GetDataProvider(connectionString: opts.ConnectionString),
                opts.ConnectionString);
        }, lifetime));
        if (runMigrations)
        {
            services.AddConfigSection<DbConfigOptions<PechkaMigrationInternalConfiguration>>(configSection);
            services.AddSingleton(sp => new PechkaMigrationInternalConfiguration
            {
                Config = sp.GetRequiredService<DbConfigOptions<PechkaMigrationInternalConfiguration>>()
            });
        }
        else
            services.AddSingleton<PechkaMigrationInternalConfiguration>();

        return services;
    }
}