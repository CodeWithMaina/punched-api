namespace PunchedApi.Domain.Entities;

/// <summary>
/// Direction of a <see cref="StampTransaction"/> relative to the customer's
/// card. Stored as an int but read through <see cref="StampTransactions"/> helpers.
/// </summary>
public enum StampTransactionDirection
{
    /// <summary>Stamps added to the card (expanding balance).</summary>
    Credit = 1,

    /// <summary>Stamps removed from the card (consume/reverse).</summary>
    Debit = 2
}
