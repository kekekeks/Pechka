using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Pechka.AspNet.BackgroundServices;

public class PechkaBackgroundOptions
{
    /// <summary>
    /// When false, ticking services and background workers stay registered but their loops are
    /// not started on host start; tests drive them deterministically via
    /// ITickingServiceManager.SyncAllServices and IBackgroundJobDispatcher.RunPendingJobsAsync.
    /// </summary>
    public bool AutoStart { get; set; } = true;
}

public static class PechkaBackgroundServiceCollectionExtensions
{
    /// <summary>
    /// Disables automatic startup of ticking services and background workers for this host,
    /// typically from a test. See <see cref="PechkaBackgroundOptions.AutoStart"/>.
    /// </summary>
    public static IServiceCollection DisablePechkaBackgroundAutoStart(this IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PechkaBackgroundOptions));
        if (existing == null)
            services.AddSingleton(new PechkaBackgroundOptions { AutoStart = false });
        else
            ((PechkaBackgroundOptions)existing.ImplementationInstance!).AutoStart = false;
        return services;
    }
}
