using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Pechka.AspNet.Jobs;

internal abstract class BackgroundJobRegistration
{
    protected BackgroundJobRegistration(string identifier, Type jobType)
    {
        Identifier = identifier;
        JobType = jobType;
    }

    public string Identifier { get; }
    public Type JobType { get; }

    public abstract Task Invoke(IServiceProvider services, string? payload, CancellationToken token);
}

internal sealed class BackgroundJobRegistration<TJob, THandler> : BackgroundJobRegistration
    where THandler : class, IBackgroundJobHandler<TJob>
{
    public BackgroundJobRegistration(string identifier) : base(identifier, typeof(TJob))
    {
    }

    public override Task Invoke(IServiceProvider services, string? payload, CancellationToken token)
    {
        var job = payload == null ? default! : JsonConvert.DeserializeObject<TJob>(payload)!;
        return services.GetRequiredService<THandler>().Execute(job, token);
    }
}

internal sealed class BackgroundJobRegistry
{
    private readonly Dictionary<string, BackgroundJobRegistration> _byIdentifier = new();
    private readonly Dictionary<Type, BackgroundJobRegistration> _byJobType = new();

    public BackgroundJobRegistry(IEnumerable<BackgroundJobRegistration> registrations)
    {
        foreach (var r in registrations)
        {
            if (!_byIdentifier.TryAdd(r.Identifier, r))
                throw new InvalidOperationException($"Duplicate background job identifier '{r.Identifier}'");
            if (!_byJobType.TryAdd(r.JobType, r))
                throw new InvalidOperationException($"Background job type {r.JobType} is registered more than once");
        }
    }

    public BackgroundJobRegistration? TryGetByIdentifier(string identifier)
        => _byIdentifier.GetValueOrDefault(identifier);

    public BackgroundJobRegistration GetByJobType(Type jobType)
        => _byJobType.TryGetValue(jobType, out var r)
            ? r
            : throw new InvalidOperationException(
                $"Background job type {jobType} is not registered, use AddBackgroundJob");
}
