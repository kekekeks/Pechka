using System.Threading.Tasks;
using LinqToDB;
using Newtonsoft.Json;
using Pechka.AspNet.Database;

namespace Pechka.AspNet.Jobs;

internal sealed class BackgroundJobScheduler<TContextManager> : IBackgroundJobScheduler
    where TContextManager : class, ITransactionalDbContextManager, IUntypedDbContextManager
{
    private readonly TContextManager _manager;
    private readonly BackgroundJobRegistry _registry;
    private readonly BackgroundJobPoller<TContextManager> _poller;

    public BackgroundJobScheduler(TContextManager manager, BackgroundJobRegistry registry,
        BackgroundJobPoller<TContextManager> poller)
    {
        _manager = manager;
        _registry = registry;
        _poller = poller;
    }

    public async Task<long> Enqueue<TJob>(TJob job)
    {
        var registration = _registry.GetByJobType(typeof(TJob));
        var row = new PechkaJobRow
        {
            Type = registration.Identifier,
            Payload = JsonConvert.SerializeObject(job),
            State = JobState.Pending,
            CreatedAt = JobTime.UtcNow
        };
        var id = await _manager.ExecUntypedAsync(dc => dc.InsertWithInt64IdentityAsync(row));
        // Wake the poller once the row is actually visible to it
        if (_manager.CurrentTransaction is { } tx)
            tx.OnCommitted(_poller.ExpediteTick);
        else
            _poller.ExpediteTick();
        return id;
    }
}
