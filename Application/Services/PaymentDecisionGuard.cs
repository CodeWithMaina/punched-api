using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <inheritdoc />
public sealed class PaymentDecisionGuard : IPaymentDecisionGuard
{
    private readonly ApplicationDbContext _context;

    public PaymentDecisionGuard(ApplicationDbContext context) => _context = context;

    public async Task<IPaymentDecisionCommit> BeginAsync(
        Guid paymentId, CancellationToken cancellationToken = default)
    {
        var isPostgres =
            _context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        if (!isPostgres)
            return new NoopCommit();

        var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Transaction-scoped: released automatically on commit/rollback, so a crashed request can
            // never leave a payment permanently locked. Two values because pg_advisory_xact_lock takes
            // a 64-bit key; the payment GUID's first 8 bytes are plenty of entropy and stable.
            var keyBytes = paymentId.ToByteArray();
            var key = BitConverter.ToInt64(keyBytes, 0);

            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        return new TransactionCommit(transaction);
    }

    private sealed class TransactionCommit : IPaymentDecisionCommit
    {
        private IDbContextTransaction? _transaction;

        public TransactionCommit(IDbContextTransaction transaction) => _transaction = transaction;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (_transaction == null) return;
            await _transaction.CommitAsync(cancellationToken);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction == null) return;
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    /// <summary>Used when the provider has no advisory locks: the guard is a no-op, not a silent failure.</summary>
    private sealed class NoopCommit : IPaymentDecisionCommit
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
