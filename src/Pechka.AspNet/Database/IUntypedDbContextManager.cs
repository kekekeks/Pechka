using System;
using System.Threading.Tasks;
using LinqToDB.Data;

namespace Pechka.AspNet.Database;

/// <summary>
/// Lets framework plumbing run queries on a context manager without knowing the concrete
/// context type. Calls are routed through the manager's regular Exec pipeline, so they join
/// an active transaction scope when there is one.
/// </summary>
public interface IUntypedDbContextManager
{
    Task<T> ExecUntypedAsync<T>(Func<DataConnection, Task<T>> cb);
}
