using System.Threading;
using System.Threading.Tasks;

namespace Pechka.AspNet.Jobs;

/// <summary>
/// Executes background jobs of type <typeparamref name="TJob"/>. Registered scoped via
/// AddBackgroundJob; each execution runs in a fresh DI scope wrapped in its own unit of work
/// (committed on success, rolled back when Execute throws).
/// </summary>
public interface IBackgroundJobHandler<TJob>
{
    Task Execute(TJob job, CancellationToken token);
}
