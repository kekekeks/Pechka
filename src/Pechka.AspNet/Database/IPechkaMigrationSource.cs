using System;
using System.Collections.Generic;
using System.Reflection;

namespace Pechka.AspNet.Database;

/// <summary>
/// Contributes framework-owned migrations to the app's migration run. Only the listed
/// migration types are discovered, so optional framework features can ship migrations
/// that stay inert unless the feature is registered.
/// </summary>
internal interface IPechkaMigrationSource
{
    Assembly Assembly { get; }
    IReadOnlyList<Type> MigrationTypes { get; }
}

internal sealed class PechkaMigrationSource : IPechkaMigrationSource
{
    public PechkaMigrationSource(Assembly assembly, IReadOnlyList<Type> migrationTypes)
    {
        Assembly = assembly;
        MigrationTypes = migrationTypes;
    }

    public Assembly Assembly { get; }
    public IReadOnlyList<Type> MigrationTypes { get; }
}
