namespace PunchedApi.Application.Services;

/// <summary>
/// Serialises the two owner decisions on one payment (verify / reject).
///
/// <para>Two people can press Approve at the same instant. The unique <c>source_payment_id</c> stops
/// double income, but it does not stop two verification events, two appointment transitions, or two
/// settlement paths running side by side. On PostgreSQL this guard takes a transaction-scoped
/// advisory lock keyed by the payment id, so the second request waits, then sees the decided status
/// and returns the idempotent answer. Providers without advisory locks (SQLite in tests) get a no-op:
/// their serialisation is irrelevant to production correctness and the financial constraints still
/// hold.</para>
/// </summary>
public interface IPaymentDecisionGuard
{
    Task<IPaymentDecisionCommit> BeginAsync(Guid paymentId, CancellationToken cancellationToken = default);
}

/// <summary>The locked decision scope. Commit releases the seat; dispose without committing rolls back.</summary>
public interface IPaymentDecisionCommit : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
