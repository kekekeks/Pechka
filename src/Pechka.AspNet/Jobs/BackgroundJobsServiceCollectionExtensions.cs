using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Pechka.AspNet.BackgroundServices;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Jobs;

public static class BackgroundJobsServiceCollectionExtensions
{
    /// <summary>
    /// Enables the database-backed background job system on top of the given transactional context
    /// manager: creates the BackgroundJobs table (via the app's migration run), registers the scoped
    /// <see cref="IBackgroundJobScheduler"/> and a single polling worker executing jobs in FIFO order,
    /// each in its own unit of work. Failed (or crash-orphaned Running) jobs don't block the queue and
    /// can be restarted by setting the row's State back to 0. One job store per app; repeated calls
    /// only re-apply <paramref name="configure"/>.
    /// </summary>
    public static IServiceCollection AddBackgroundJobs<TContextManager>(this IServiceCollection services,
        Action<PechkaBackgroundJobOptions>? configure = null)
        where TContextManager : class, ITransactionalDbContextManager, IUntypedDbContextManager
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PechkaBackgroundJobOptions));
        PechkaBackgroundJobOptions options;
        if (existing == null)
        {
            options = new PechkaBackgroundJobOptions();
            services.AddSingleton(options);
            services.AddSingleton<BackgroundJobRegistry>();
            services.AddScoped<IBackgroundJobScheduler, BackgroundJobScheduler<TContextManager>>();
            services.AddSingleton<BackgroundJobPoller<TContextManager>>();
            // Intent only: the poller is started by PechkaBackgroundWorkersRunner under the "services" role
            services.AddSingleton<IPechkaBackgroundWorker>(sp =>
                sp.GetRequiredService<BackgroundJobPoller<TContextManager>>());
            services.AddSingleton<IPechkaMigrationSource>(new PechkaMigrationSource(
                typeof(PechkaBackgroundJobsMigration).Assembly, new[] { typeof(PechkaBackgroundJobsMigration) }));
        }
        else
            options = (PechkaBackgroundJobOptions)existing.ImplementationInstance!;
        configure?.Invoke(options);
        return services;
    }

    /// <summary>
    /// Registers a background job type with its handler. <paramref name="identifier"/> is stored in
    /// the job rows to locate the handler and defaults to the job type's full name.
    /// <paramref name="retryTransientFailures"/> opts the job into in-process retries of transient
    /// database failures (per PechkaDbTransactionOptions retry settings) before it is marked Failed;
    /// leave off for handlers with side effects outside the unit of work.
    /// </summary>
    public static IServiceCollection AddBackgroundJob<TJob, THandler>(this IServiceCollection services,
        string? identifier = null, bool retryTransientFailures = false)
        where THandler : class, IBackgroundJobHandler<TJob>
    {
        services.AddScoped<THandler>();
        services.AddSingleton<BackgroundJobRegistration>(
            new BackgroundJobRegistration<TJob, THandler>(identifier ?? typeof(TJob).FullName!,
                retryTransientFailures));
        return services;
    }
}
