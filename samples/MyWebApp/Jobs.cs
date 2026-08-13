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
