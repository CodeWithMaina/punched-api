namespace PunchedApi.Application.Services;

/// <summary>
/// Tunables for the payment → ledger outbox and its reconciliation sweep.
///
/// <para>Defaults are deliberately conservative: a settled payment is normally written to the books
/// in the same request, so the worker is a safety net — but when it is needed it should act within
/// seconds, not hours. Backoff is bounded so a transient database outage never turns into a tight
/// retry loop, and attempts are capped so a permanently bad row becomes visible instead of spinning
/// forever.</para>
/// </summary>
public sealed class PaymentLedgerOptions
{
    public const string SectionName = "PaymentLedger";

    /// <summary>Run the outbox worker. The API-only role sets WORKERS_ENABLED=false instead.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Obligations claimed per batch.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Seconds between outbox polls. Not a busy loop: the default is 15 seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>How long a claim lease lasts before a crashed worker's row is reclaimed.</summary>
    public int StaleProcessingMinutes { get; set; } = 3;

    /// <summary>Attempts allowed before a transient failure is parked as failed.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base for exponential backoff (attempt 1 waits this long, then doubles).</summary>
    public int BaseBackoffSeconds { get; set; } = 15;

    /// <summary>Backoff ceiling, so a row is retried at least this often while attempts remain.</summary>
    public int MaxBackoffSeconds { get; set; } = 900;

    /// <summary>Minutes between reconciliation sweeps (the safety net for missed obligations).</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 30;

    /// <summary>Rows examined per reconciliation pass (bounded, so a big tenant cannot stall a run).</summary>
    public int ReconciliationBatchSize { get; set; } = 100;

    /// <summary>Run one reconciliation pass shortly after startup, then on the interval.</summary>
    public bool ReconcileOnStartup { get; set; } = true;
}
