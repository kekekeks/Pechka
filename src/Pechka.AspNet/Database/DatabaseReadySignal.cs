using System.Threading.Tasks;

namespace Pechka.AspNet.Database;

/// <summary>
/// Completed once startup migrations have run, so database-polling services can hold their
/// first tick until the schema exists. Created pre-completed in processes that don't run
/// migrations (no web role), where the schema is assumed to be managed elsewhere.
/// </summary>
internal sealed class DatabaseReadySignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DatabaseReadySignal(bool ready)
    {
        if (ready)
            _tcs.TrySetResult();
    }

    public Task Ready => _tcs.Task;

    public void Set() => _tcs.TrySetResult();
}
