using System;

namespace Pechka.AspNet.Jobs;

public class PechkaBackgroundJobOptions
{
    /// <summary>
    /// Fallback poll interval for pending jobs. Enqueues additionally wake the poller as soon as
    /// the enqueuing transaction commits, so this mostly matters for jobs restarted via the database.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Successfully completed job rows older than this are deleted; null keeps them forever.</summary>
    public TimeSpan? CompletedJobRetention { get; set; }
}
