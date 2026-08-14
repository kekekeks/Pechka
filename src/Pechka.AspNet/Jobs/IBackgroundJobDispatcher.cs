using System.Threading;
using System.Threading.Tasks;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Manual, deterministic control over the background job queue, intended for tests
/// (typically combined with DisablePechkaBackgroundAutoStart).
/// </summary>
public interface IBackgroundJobDispatcher
{
    /// <summary>
    /// Runs one full FIFO drain of the job queue inline (serialized with the live poller loop,
    /// if one is running) and returns the number of jobs processed — completed or failed. With
    /// a concurrently running automatic loop the count may include jobs it processed.
    /// </summary>
    Task<int> RunPendingJobsAsync(CancellationToken token = default);
}
