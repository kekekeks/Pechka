using System;

namespace Pechka.AspNet.Database;

/// <summary>
/// Opts an RPC method/class or MVC action/controller out of automatic transient-failure retries
/// (see PechkaDbTransactionOptions.EnableRetries). Use on handlers with side effects outside the
/// unit of work, e.g. direct external service calls.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoRetryAttribute : Attribute
{
}
