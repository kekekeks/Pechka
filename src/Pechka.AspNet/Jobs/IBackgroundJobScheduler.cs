using System;
using System.Threading.Tasks;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Enqueues background jobs into the database-backed FIFO queue. The insert goes through the
/// job store's context manager, so inside an active unit of work the job becomes visible (and
/// executable) only when that transaction commits, and disappears with it on rollback.
/// </summary>
public interface IBackgroundJobScheduler
{
    /// <summary>
    /// Enqueues a job and returns its queue id. The job type must be registered via
    /// AddBackgroundJob. <paramref name="expiresIn"/> overrides the job type's default expiration:
    /// a job not started within that time is auto-failed.
    /// </summary>
    Task<long> Enqueue<TJob>(TJob job, TimeSpan? expiresIn = null);
}
