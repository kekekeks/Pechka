using System;
using System.Data;
using LinqToDB.Data;
using LinqToDB.DataProvider;
using LinqToDB.DataProvider.PostgreSQL;
using LinqToDB.DataProvider.SqlServer;
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
    {
        services.AddConfigSection<DbConfigOptions<TContextManager>>(configSection);
        services.AddSingleton<TContextManager>(sp =>
        {
            var opts = sp.GetRequiredService<DbConfigOptions<TContextManager>>();
            if (opts.Type == DatabaseType.SqlServer)
                return factory(
                    new SqlServerDataProvider("app", SqlServerVersion.v2017, SqlServerProvider.MicrosoftDataSqlClient),
                    opts.ConnectionString);
            return factory(new PostgreSQLDataProvider(), opts.ConnectionString);
        });
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