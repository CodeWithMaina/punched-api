using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// Database connection telemetry, in the same <c>System.Diagnostics.Metrics</c> style
/// as the rest of the application: connected-but-idle until an exporter subscribes.
///
/// <para>These are the numbers needed to answer the Neon questions that matter —
/// "how long does acquiring a connection take?", "are we getting connection
/// failures?", "are we exhausting the pool?" — without adding a monitoring stack.</para>
/// </summary>
public sealed class DatabaseRuntimeMetrics : IDisposable
{
    private readonly Meter _meter = new("PunchedApi.Database");
    private readonly Histogram<double> _acquireDuration;
    private readonly Counter<long> _acquisitions;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _poolExhaustions;

    public DatabaseRuntimeMetrics()
    {
        _acquisitions = _meter.CreateCounter<long>(
            "db_connection_acquisitions_total", description: "Connections obtained from the Npgsql pool.");
        _failures = _meter.CreateCounter<long>(
            "db_connection_failures_total", description: "Connection attempts that failed, by failure kind.");
        _poolExhaustions = _meter.CreateCounter<long>(
            "db_pool_exhaustion_total", description: "Connection attempts that timed out waiting for a free pooled connection.");
        _acquireDuration = _meter.CreateHistogram<double>(
            "db_connection_acquisition_ms", "ms",
            "Time to obtain an open connection (pool hit, or TCP+TLS+auth on a miss).");
    }

    public void RecordAcquired(double durationMs)
    {
        _acquisitions.Add(1);
        _acquireDuration.Record(durationMs);
    }

    public void RecordFailure(string kind, bool poolExhausted)
    {
        _failures.Add(1, new KeyValuePair<string, object?>("kind", kind));
        if (poolExhausted) _poolExhaustions.Add(1);
    }

    public void Dispose() => _meter.Dispose();
}
