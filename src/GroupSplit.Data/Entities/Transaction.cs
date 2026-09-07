namespace GroupSplit.Data.Entities;

/// <summary>
/// Money that moved: either something the group spent (<see cref="Expense"/>) or one
/// member paying another back (<see cref="Transfer"/>).
/// </summary>
/// <remarks>
/// One table, one level of inheritance, shared columns here and leaf-only columns on the
/// leaves -- where they are nullable in the table by construction, which is expected
/// rather than a smell. What went wrong with <c>RuleVersion</c> was TPT: four
/// tables and a join per read, over a three-level chain. This is EF's default mapping and
/// a single table.
/// <para>
/// The discriminator is the discriminator. There is no <c>Kind</c> property beside it;
/// branch with <c>is Transfer</c> or, better, read <c>Set&lt;Expense&gt;()</c>, which EF
/// filters for you and which therefore cannot be forgotten the way an extension method
/// can.
/// </para>
/// </remarks>
public abstract class Transaction : Entity
{
    /// <summary>
    /// Who paid.
    /// </summary>
    public virtual User User { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>
    /// The group this belongs to.
    /// </summary>
    /// <remarks>
    /// Nullable because in the finished model a personal transaction has no group -- that
    /// is what personal means. Nothing writes null yet: every transaction still sits in a
    /// group, including the hidden personal one, until the step that deletes those.
    /// </remarks>
    public virtual Group? Group { get; set; }

    public Guid? GroupId { get; set; }

    public required decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217. Must match the group's -- conversion is out of scope, and a group whose
    /// balances silently mixed currencies would be wrong rather than incomplete.
    /// </summary>
    public string Currency { get; set; } = Currencies.Default;

    public required DateTimeOffset DateTime { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The imported row this was filed from, when it was. The one trace on the ledger of
    /// where a transaction came from; null for one somebody typed in.
    /// </summary>
    /// <remarks>
    /// Set by filing and by nothing else: a sync that later modifies the bank row does
    /// not follow this link, and an edit to this transaction does not follow it back.
    /// Unlinking a bank sets it to null and leaves everything else as it was. Deferred
    /// from Phase 1, where it would have been a column with no writer.
    /// </remarks>
    public virtual BankTransaction? BankTransaction { get; set; }

    public Guid? BankTransactionId { get; set; }

    /// <summary>
    /// What each person owed on this. Sums to <see cref="Amount"/>, always.
    /// </summary>
    public virtual ICollection<TransactionSplit> Splits { get; } = [];
}
