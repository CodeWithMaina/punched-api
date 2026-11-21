using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// Measures how long opening a pooled connection takes and classifies failures.
///
/// <para>The distinction matters on Neon: a <b>cold start</b> (suspended compute,
/// then TCP+TLS+auth) shows up as a long-but-successful acquisition, whereas a
/// <b>pool exhaustion</b> is a timeout while every socket is busy. Only the second one
/// means the connection budget is wrong.</para>
///
/// <para>Timing state is keyed by the <see cref="DbConnection"/> instance being opened.
/// Concurrent opens use different instances, so this is safe for a singleton
/// interceptor without per-call state on the instance itself.</para>
/// </summary>
public sealed class DatabaseConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ConcurrentDictionary<DbConnection, long> _opening = new();
    private readonly DatabaseRuntimeMetrics _metrics;
    private readonly ILogger<DatabaseConnectionInterceptor> _logger;

    public DatabaseConnectionInterceptor(
        DatabaseRuntimeMetrics metrics,
        ILogger<DatabaseConnectionInterceptor> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        Remember(connection);
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Remember(connection);
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }

    /// <summary>
    /// Records the open timestamp, with a hard bound on the map: a path that opens a
    /// connection but never reports success or failure must not grow this forever.
    /// </summary>
    private void Remember(DbConnection connection)
    {
        if (_opening.Count > 1024) _opening.Clear();
        _opening[connection] = Stopwatch.GetTimestamp();
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Record(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
    {
        RecordFailure(connection, eventData.Exception);
        base.ConnectionFailed(connection, eventData);
    }

    public override Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RecordFailure(connection, eventData.Exception);
        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
    }

    private void Record(DbConnection connection)
    {
        if (!_opening.TryRemove(connection, out var startedAt)) return;

        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        _metrics.RecordAcquired(elapsedMs);

        // A slow-but-successful acquisition on a serverless database is a wake-up, not
        // a fault. Log it at Information so a cold start is visible after the fact.
        if (elapsedMs >= 1000)
        {
            _logger.LogInformation(
                "Database connection opened in {ElapsedMs:0} ms — the signature of a suspended compute waking up.",
                elapsedMs);
        }
        else
        {
            _logger.LogDebug("Database connection opened in {ElapsedMs:0.0} ms.", elapsedMs);
        }
    }

    private void RecordFailure(DbConnection connection, Exception exception)
    {
        _opening.TryRemove(connection, out _);

        var poolExhausted = IsPoolExhaustion(exception);
        var kind = poolExhausted
            ? "pool_exhaustion"
            : exception switch
            {
                TimeoutException => "timeout",
                Npgsql.NpgsqlException { InnerException: TimeoutException } => "timeout",
                Npgsql.PostgresException => "server_error",
                _ => exception.GetType().Name
            };

        _metrics.RecordFailure(kind, poolExhausted);

        if (poolExhausted)
        {
            _logger.LogError(
                exception,
                "The Npgsql connection pool is exhausted. Every pooled connection is busy; check Database:MaximumPoolSize "
                + "against the connection budget, and whether a worker is holding a connection while waiting on an external service.");
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Database connection failed ({Kind}). The pool discards the broken socket and the next attempt reconnects.",
                kind);
        }
    }

    /// <summary>Npgsql reports pool exhaustion as a timeout whose message names the pool.</summary>
    private static bool IsPoolExhaustion(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("pool has been exhausted", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("Timeout while getting a connection", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
