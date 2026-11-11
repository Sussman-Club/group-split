namespace GroupSplit.Data.Entities;

/// <summary>
/// One person's answer of "no, these are not the same money", for one imported row and one
/// expense.
/// </summary>
/// <remarks>
/// A suggestion is only ever a suggestion, so the app has to remember the answers it was
/// given: without this row the same pair would be raised again on the next listing, and a
/// suggestion that cannot be got rid of is worse than none.
/// <para>
/// It is the pair that is dismissed and not the row: a coffee that really was bought twice
/// still wants the second charge suggested against the second expense. The pair is unique,
/// and cascades from both sides -- delete either the row or the expense and there is no
/// longer a pair to have an opinion about.
/// </para>
/// </remarks>
public class BankMatchDismissal : Entity
{
    public virtual BankTransaction BankTransaction { get; set; } = null!;

    public Guid BankTransactionId { get; set; }

    public virtual Transaction Transaction { get; set; } = null!;

    public Guid TransactionId { get; set; }

    public required DateTimeOffset DismissedAt { get; set; }
}
