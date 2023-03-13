using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Pechka.AspNet.BackgroundServices
{
    internal class ServiceRunnerRegistry
    {
        private readonly List<Type> _serviceTypes;
        public IReadOnlyList<Type> ServiceTypes => _serviceTypes;

        public ServiceRunnerRegistry(Assembly assembly)
        {
            _serviceTypes = assembly.GetTypes()
                .Where(typeof(TickingServiceBase).IsAssignableFrom)
                .Where(t => !t.IsAbstract).ToList();
        }

        public void Register(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton(this);
            foreach (var t in _serviceTypes)
                serviceCollection.AddSingleton(t);
        }
    }
    
    internal class TickingServiceManager : IHostedService, ITickingServiceManager
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IReadOnlyList<Type> _serviceTypes;

        public TickingServiceManager(ServiceRunnerRegistry registry, IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _serviceTypes = registry.ServiceTypes;
        }
        
        public static void StartServices(IServiceProvider provider)
        {
            
        }

        public Task SyncAllServices(CancellationToken token) =>
            Task.WhenAll(_serviceTypes
                .Select(s =>
                    Task.Run(() => ((TickingServiceBase)_serviceProvider.GetRequiredService(s)).ForceSync(token),
                        token)).ToArray());

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var lifetime = _serviceProvider.GetRequiredService<IHostApplicationLifetime>();
            var logger = _serviceProvider.GetRequiredService<ILoggerFactory>();
            foreach (var st in _serviceTypes)
            {
                var service = (TickingServiceBase)_serviceProvider.GetService(st);
                service.Start(lifetime, logger);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public interface ITickingServiceManager
    {
        Task SyncAllServices(CancellationToken token);
    }
    
}