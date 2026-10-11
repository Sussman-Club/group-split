namespace GroupSplit.Shared;

/// <summary>
/// One person's share of an expense, as it is stored.
/// </summary>
/// <param name="UserId">
/// Who owes it. Carried beside the name because the edit dialog sends these back as
/// <see cref="SplitInput"/>, and a name is not something the API can be addressed by.
/// </param>
/// <param name="IsGuest">
/// True when the share belongs to one of the group's guests. The share itself is ordinary --
/// it counts in the balances like any other -- but who holds it is worth showing, because
/// nobody should read the name as a member who joined.
/// </param>
public record TransactionSplitResponse(
    Guid UserId,
    string UserName,
    decimal Amount,
    bool IsGuest = false)
{
    /// <summary>The name <see cref="IsGuest"/> had before guests were renamed; still written for older clients.</summary>
    [Obsolete("Use IsGuest.")]
    public bool IsPendingInvitee
    {
        get => IsGuest;
        init => IsGuest = value;
    }
}

public record TransactionDetailsResponse : TransactionResponse
{
    public List<TransactionSplitResponse> Splits { get; init; } = [];

    /// <summary>
    /// The version of the rule that divided it, or null when no rule did -- shares somebody
    /// typed, or an even division under no rule at all.
    /// </summary>
    /// <remarks>
    /// Carried so a client can tell where the shares above came from, which the amounts
    /// alone do not say. The edit dialog needs it to open its split control on the truth:
    /// without it every expense read as automatically divided, and "Divided by the Rent
    /// rule" sat over amounts somebody had typed by hand.
    /// <para>
    /// Null is two answers rather than one, and the model does not separate them: an even
    /// division records no version either. A client that has to choose treats null as
    /// "these amounts are the expense's own", which is true of both -- the automatic
    /// division is reachable from there by asking for it.
    /// </para>
    /// </remarks>
    public Guid? SplitRuleVersionId { get; init; }

    /// <summary>
    /// The rule the expense named for itself, whatever its category says, or null when it
    /// named none. See <see cref="CreateTransactionRequest.SplitRuleId"/>.
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="SplitRuleVersionId"/>, which is what divided it:
    /// an expense divided by its category's rule carries a version and no rule of its own.
    /// The edit dialog reads this one to open its split control on the right answer -- "all
    /// for Ana" rather than "automatically" -- and to know what the person is changing away
    /// from when they pick something else.
    /// </remarks>
    public Guid? SplitRuleId { get; init; }

    /// <summary>
    /// On a settlement, the last day of activity it was meant to settle, as stored. Null on
    /// an expense, and on a settlement that claims no period.
    /// </summary>
    public DateOnly? CoversThrough { get; init; }

    /// <summary>
    /// On a settlement made through a day, what that period is and whether it is still
    /// square, on the reader's calendar. Only <c>GET /transactions/{id}</c> fills it, since
    /// only it is told the reader's offset.
    /// </summary>
    public SettlementCoverage? Coverage { get; init; }
}

/// <summary>
/// The period a settlement was for, worked out when it is read and never stored.
/// </summary>
/// <remarks>
/// Derived rather than remembered, which is the point: an expense dated inside the period and
/// added afterwards, or one edited or deleted, is reflected here the next time somebody
/// looks, where a stored list of "covered" expenses would go on claiming they were paid.
/// </remarks>
/// <param name="Through">The last day of activity it was meant to settle.</param>
/// <param name="From">
/// The first day of the period: the day after the last one an earlier settlement the payer
/// was part of, with anybody, covered through, or null when there was none -- since the
/// group began.
/// </param>
/// <param name="StillOwed">
/// What the payer still owes the recipient for activity through <paramref name="Through"/>,
/// read as the balances through that day read it now: settlements made for later count as
/// far as they pay the period down. Zero means the period is square between them; more
/// than zero usually means something dated inside it was added or changed since it was
/// settled, or it was paid short.
/// </param>
public record SettlementCoverage(DateOnly Through, DateOnly? From, decimal StillOwed);
