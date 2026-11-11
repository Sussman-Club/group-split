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

    /// <summary>
    /// The provider's id for the account, unique within the connection and nowhere wider:
    /// it is minted per item, so the same bank account linked twice arrives under two
    /// different ids.
    /// </summary>
    /// <remarks>
    /// Which is what makes a re-link look like a bank full of accounts nobody has seen --
    /// see <see cref="BankConnection.AccountsRekeyed"/>, the one run that matches on what
    /// rows look like instead.
    /// </remarks>
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

    /// <summary>
    /// The person withdrew this one account at the bank, leaving the rest of the connection
    /// working. Nothing new will arrive for it; what it already brought in stays, because
    /// that is still where the money went.
    /// </summary>
    /// <remarks>
    /// Account-level revocation is a different thing from
    /// <see cref="BankConnectionStatus.Revoked"/>, which is the whole connection. Without
    /// this the account goes on looking healthy while its data quietly stops being real.
    /// Cleared if the provider reports the account again, which is what re-sharing it looks
    /// like from here.
    /// </remarks>
    public bool AccessRevoked { get; set; }

    public virtual ICollection<BankTransaction> Transactions { get; } = [];
}
