namespace GroupSplit.Shared;

/// <summary>
/// What each part of a charge would come to, asked while somebody is still deciding which
/// lines belong to which purchase.
/// </summary>
/// <remarks>
/// The screen that splits a charge has to show what each part is worth as it is built --
/// that figure is the reason to move a line from one part to another. Working it out in the
/// client would mean a second copy of the apportioning, which is exactly the arithmetic that
/// must not drift: tax weighed over the lines it was charged on, tip over all of them, each
/// cut truncated to the cent with the leftover to one named holder.
/// <para>
/// So it is asked rather than computed, the same way the expense dialogs ask what a division
/// would come to before storing it. Nothing is created and nothing is checked beyond the
/// lines being this bill's.
/// </para>
/// <para>
/// Deliberately laxer than <see cref="SplitBankTransactionRequest"/>: a part with nothing in
/// it yet, a single part, a bill half placed. Those are ordinary states of a screen somebody
/// is working in, and refusing to price them would leave the figures blank until the moment
/// they stop being needed.
/// </para>
/// </remarks>
public record SplitChargePreviewRequest
{
    /// <summary>The proposed purchases, each as the lines it holds, in order.</summary>
    public IReadOnlyList<SplitChargePreviewPartInput> Parts { get; init; } = [];
}

/// <summary>One proposed purchase, as the lines of the bill it holds.</summary>
public record SplitChargePreviewPartInput
{
    public IReadOnlyList<Guid> ItemIds { get; init; } = [];
}

/// <summary>
/// What the parts come to, and what is still unaccounted for.
/// </summary>
/// <param name="Amounts">
/// One figure per part, in the order they were sent. What each expense would be created for.
/// </param>
/// <param name="Charge">What the card was charged, which the parts have to sum to before the split can go ahead.</param>
/// <param name="Placed">
/// What the parts come to together. Short of <paramref name="Charge"/> exactly when some of
/// the bill is in no part yet -- which is the figure the screen leads with, since it is the
/// one thing standing between a half-built split and a filed one.
/// </param>
public sealed record SplitChargePreviewResponse(
    IReadOnlyList<decimal> Amounts,
    decimal Charge,
    decimal Placed)
{
    /// <summary>What is still in no part. Zero when every line has one.</summary>
    public decimal Unplaced => Charge - Placed;
}
