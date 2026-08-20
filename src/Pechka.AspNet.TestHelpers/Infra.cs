using System.Net;
using System.Net.Sockets;

namespace Pechka.AspNet.TestHelpers;

public static class TestPorts
{
    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public static class RepoRoot
{
    /// <summary>Walks up from the test binaries to the directory containing the given marker file
    /// (typically the solution file).</summary>
    public static string Find(string markerFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, markerFileName)))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   $"{markerFileName} not found above {AppContext.BaseDirectory}");
    }
}

/// <summary>Convergence helper: poll a condition instead of sleeping.</summary>
public static class Poll
{
    public static async Task UntilAsync(Func<Task<bool>> condition, int timeoutSeconds = 30, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                    return;
                last = null;
            }
            catch (Exception e)
            {
                last = e;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Condition {what ?? "unnamed"} not met in {timeoutSeconds}s", last);
    }
}
