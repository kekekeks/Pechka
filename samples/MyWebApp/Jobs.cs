using LinqToDB;
using Pechka.AspNet.Jobs;

namespace MyWebApp;

public class GreetingJob
{
    public string Name { get; set; } = "";
}

// Runs in its own unit of work: the TodoItem insert and the job's Completed state
// commit atomically.
public class GreetingJobHandler : IBackgroundJobHandler<GreetingJob>
{
    private readonly MyDbContextManager _db;

    public GreetingJobHandler(MyDbContextManager db) => _db = db;

    public Task Execute(GreetingJob job, CancellationToken token)
        => _db.ExecAsync(db => db.InsertAsync(new TodoItem { Name = $"greeting-{job.Name}" }, token: token));
}

public class FlakyJob
{
    public string Reason { get; set; } = "";
}

// Always throws: demonstrates a Failed row with the stored exception; restart it with
// UPDATE "BackgroundJobs" SET "State" = 0 WHERE ... and watch Attempts grow.
public class FlakyJobHandler : IBackgroundJobHandler<FlakyJob>
{
    public Task Execute(FlakyJob job, CancellationToken token)
        => throw new InvalidOperationException($"Flaky job failed intentionally: {job.Reason}");
}

public class TransientJob
{
    public string Name { get; set; } = "";
}

// Registered with retryTransientFailures: true — the probe's 40001 on the first attempt is
// retried in-process instead of failing the job.
public class TransientJobHandler : IBackgroundJobHandler<TransientJob>
{
    private readonly MyDbContextManager _db;

    public TransientJobHandler(MyDbContextManager db) => _db = db;

    public async Task Execute(TransientJob job, CancellationToken token)
    {
        await RetryProbe.FailEveryOtherAttempt(_db);
        await _db.ExecAsync(db => db.InsertAsync(new TodoItem { Name = $"transient-{job.Name}" }, token: token));
    }
}
