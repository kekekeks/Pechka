using System.Collections.Concurrent;
using System.Diagnostics;

namespace Pechka.AspNet.TestHelpers;

/// <summary>
/// Builds a frontend once per test process (memoized per directory). Only runs when something —
/// typically <see cref="PechkaPlaywrightFixture{TApp}"/> via
/// <see cref="PechkaTestApp.EnsureFrontendBuiltAsync"/> — actually asks, so backend-only runs
/// never pay for npm. The in-process host serves the build output directory live, so the build
/// only has to finish before the first navigation. A failed build stays failed for the whole run:
/// every browser test then reports the build error instead of a misleading page-level one.
/// </summary>
public static class FrontendBuild
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Builds = new();

    /// <param name="directory">The frontend project directory (holding package.json).</param>
    /// <param name="buildCommand">Shell command producing the build output.</param>
    /// <param name="installCommand">Shell command run first when node_modules is missing.</param>
    public static Task EnsureBuiltAsync(string directory, string buildCommand = "npm run build",
        string installCommand = "npm ci")
    {
        var key = Path.GetFullPath(directory);
        return Builds.GetOrAdd(key, dir => new Lazy<Task>(
            () => Task.Run(() => Build(dir, buildCommand, installCommand)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static void Build(string directory, string buildCommand, string installCommand)
    {
        if (!Directory.Exists(Path.Combine(directory, "node_modules")))
            Run(directory, installCommand, TimeSpan.FromMinutes(5));
        Run(directory, buildCommand, TimeSpan.FromMinutes(5));
    }

    private static void Run(string workingDir, string command, TimeSpan timeout)
    {
        // Login shell so a version-manager-provided npm is on PATH.
        var psi = new ProcessStartInfo("bash", ["-lc", command])
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        // Drain both streams concurrently — a full pipe buffer deadlocks the child — but each
        // into its own string; a shared buffer is not thread-safe.
        var stdout = Task.Run(() => process.StandardOutput.ReadToEndAsync());
        var stderr = Task.Run(() => process.StandardError.ReadToEndAsync());
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{command}' in {workingDir} timed out after {timeout}");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{command}' in {workingDir} failed ({process.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
    }
}
