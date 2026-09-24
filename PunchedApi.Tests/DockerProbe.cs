namespace PunchedApi.Tests;

/// <summary>
/// Detects whether a Docker daemon is reachable, so container-backed tests can be
/// SKIPPED (with a reason in the test report) on machines without Docker instead of
/// failing red and hiding real regressions. Probed once per test run.
///
/// Overrides (useful in CI):
///   PUNCHED_DOCKER_TESTS=on   → assume Docker is available (run the tests)
///   PUNCHED_DOCKER_TESTS=off  → always skip them
/// </summary>
internal static class DockerProbe
{
    /// <summary>Reason shown in the skipped-test report.</summary>
    public const string SkipReason =
        "Requires a Docker daemon (Testcontainers PostgreSQL). Start Docker Desktop and re-run, " +
        "or set PUNCHED_DOCKER_TESTS=on to run these tests against a remote daemon.";

    /// <summary>True when a container-backed test should be allowed to run.</summary>
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        var overrideValue = Environment.GetEnvironmentVariable("PUNCHED_DOCKER_TESTS");
        if (string.Equals(overrideValue, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(overrideValue, "0", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(overrideValue, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(overrideValue, "1", StringComparison.OrdinalIgnoreCase))
            return true;

        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost))
            return HostIsReachable(dockerHost);

        // Platform defaults: Docker Desktop on Windows uses a named pipe, on
        // Linux/macOS a unix socket.
        if (OperatingSystem.IsWindows())
            return File.Exists(@"\\.\pipe\docker_engine") || File.Exists(@"\\.\pipe\docker_desktop_engine");

        return File.Exists("/var/run/docker.sock") || File.Exists("/run/docker.sock");
    }

    private static bool HostIsReachable(string dockerHost)
    {
        if (dockerHost.StartsWith("npipe:", StringComparison.OrdinalIgnoreCase))
        {
            // npipe://./pipe/docker_engine  or  npipe:////./pipe/docker_engine
            var pipe = dockerHost[(dockerHost.IndexOf("pipe", StringComparison.OrdinalIgnoreCase))..];
            pipe = pipe.Replace('/', '\\').TrimStart('\\');
            return File.Exists(@"\\.\" + pipe);
        }

        if (dockerHost.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            return File.Exists(dockerHost["unix://".Length..]);

        if (dockerHost.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(dockerHost.Replace("tcp://", "http://"), UriKind.Absolute, out var uri))
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromSeconds(1)) &&
                       client.Connected;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }
}
