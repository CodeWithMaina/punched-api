using Xunit;

namespace PunchedApi.Tests;

/// <summary>
/// [Fact] that only runs when a Docker daemon is reachable — used by the
/// Testcontainers PostgreSQL tests (set-based SQL that SQLite/InMemory cannot
/// model). When Docker is absent the test is reported as SKIPPED with
/// <see cref="DockerProbe.SkipReason"/> instead of failing, so a missing Docker
/// install never masquerades as a broken build.
///
/// xUnit v2 evaluates the attribute at discovery time, which is why the probe
/// result can be assigned to <see cref="FactAttribute.Skip"/> in the constructor.
/// </summary>
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerProbe.IsAvailable)
            Skip = DockerProbe.SkipReason;
    }
}
