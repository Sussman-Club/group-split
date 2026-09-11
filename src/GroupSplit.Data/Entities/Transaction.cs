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

    /// <summary>
    /// Stored to two decimal places, which the column enforces rather than the callers
    /// being trusted with it.
    /// </summary>
    /// <remarks>
    /// The division depends on it. <c>SplitCalculator</c> truncates every share to the
    /// cent and hands the leftover to one participant, so the shares sum to this exactly;
    /// an amount carrying a third decimal would push a fraction of a cent nobody can pay
    /// onto whoever holds the remainder, on every expense filed under the rule.
    /// </remarks>
    public required decimal Amount { get; set; }

    /// <summary>
    /// ISO 4217. Must match the group's -- conversion is out of scope, and a group whose
    /// balances silently mixed currencies would be wrong rather than incomplete.
    /// </summary>
    public string Currency { get; set; } = Currencies.Default;

    /// <summary>
    /// Always UTC: every write converts before storing, so the offset read back is zero
    /// whatever the offset it arrived with.
    /// </summary>
    /// <remarks>
    /// Which makes rendering the caller's problem, and it is a problem: an expense
    /// recorded at 12:38 in New York is stored as 17:38+00:00, and showing the date off
    /// this value without converting back moves it a day for anybody far enough east or
    /// west. The clients have a clock for that.
    /// </remarks>
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
    /// Where the money went, when it went somewhere with a name. Copied from the imported
    /// row by filing, the same way the amount and the date are.
    /// </summary>
    /// <remarks>
    /// Nullable because most transactions have no merchant and never will: a transfer is
    /// one member paying another, and an expense somebody typed in is "Dinner" and not a
    /// shop. Null here is the same kind of null as <see cref="Group"/>'s -- a fact that
    /// does not apply, rather than one that is missing and wants backfilling.
    /// <para>
    /// Copied as a link and not as a name, so a merchant whose logo the provider adds later
    /// lights up every expense already filed against it. That is the whole reason the place
    /// is a row of its own; see <see cref="Entities.Merchant"/>.
    /// </para>
    /// </remarks>
    public virtual Merchant? Merchant { get; set; }

    public Guid? MerchantId { get; set; }

    /// <summary>
    /// The division this was written under: the version its category's rule was on at the
    /// moment it was recorded. Null when nothing decided it -- a transfer, a personal
    /// expense, an expense filed under no category or under one that names no rule, or one
    /// whose shares the person typed in themselves.
    /// </summary>
    /// <remarks>
    /// The pointer that makes an edit re-divide by the rule the expense had rather than by
    /// the rule the category has now. A version is never edited and never deleted while
    /// this points at it, so the answer does not move under a transaction that has already
    /// been recorded.
    /// <para>
    /// It does not replace <see cref="Splits"/> and is not replaced by them. The splits say
    /// what each person owed -- the fact -- and survive any later edit to the rule; this
    /// says which question produced them, and survives any later edit to the amount. A
    /// recalculation needs both: one to divide by, one to check against.
    /// </para>
    /// <para>
    /// Null rather than an invented row for the cases that never had a rule, the same kind
    /// of null as <see cref="Group"/>'s: a fact that does not apply rather than one that is
    /// missing. Nothing is lost, because for those the division is either stated on the
    /// transaction or is "evenly, between whoever is in the group", which is what an absent
    /// rule has always meant.
    /// </para>
    /// </remarks>
    public virtual SplitRuleVersion? SplitRuleVersion { get; set; }

    public Guid? SplitRuleVersionId { get; set; }

    /// <summary>
    /// What each person owed on this. Sums to <see cref="Amount"/>, always.
    /// </summary>
    /// <remarks>
    /// "Owed" is the <see cref="Expense"/> reading. A <see cref="Transfer"/> goes through
    /// the same rows and means the other thing: its single split names who was <em>paid</em>,
    /// beside <see cref="User"/>, who paid. Which is why the balances can sum both kinds
    /// without knowing them apart -- paid rises on one side, owed on the other, and a
    /// repayment moves a balance exactly as far as an expense of the same size moved it the
    /// other way.
    /// </remarks>
    public virtual ICollection<TransactionSplit> Splits { get; } = [];
}
