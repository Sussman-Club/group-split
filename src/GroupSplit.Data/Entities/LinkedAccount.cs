namespace GroupSplit.Data.Entities;

/// <summary>
/// One account at a linked institution: a current account, a credit card.
/// </summary>
/// <remarks>
/// The accounts a connection has are fetched once, at exchange. A bank that later shows
/// the provider a new account is a re-link, not a sync -- see the sync rules in the
/// Phase 3 plan -- so nothing here is refreshed and nothing here is a balance.
/// </remarks>
public class LinkedAccount : Entity
{
    public virtual BankConnection Connection { get; set; } = null!;

    public Guid BankConnectionId { get; set; }

    public required string ProviderAccountId { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// The last few digits of the account number, as the bank displays them. Optional
    /// because not every institution gives one.
    /// </summary>
    public string? Mask { get; set; }

    /// <summary>
    /// The provider's word for what kind of account it is -- <c>depository</c>,
    /// <c>credit</c> -- kept as a label and not an enum, because the app does nothing
    /// with it but show it and the set is the provider's to change.
    /// </summary>
    public required string Type { get; set; }

    public string? Subtype { get; set; }

    /// <summary>
    /// ISO 4217, the account's own. Each row carries its own as well, because a provider
    /// may report one that differs.
    /// </summary>
    public string Currency { get; set; } = Currencies.Default;

    public virtual ICollection<BankTransaction> Transactions { get; } = [];
}
