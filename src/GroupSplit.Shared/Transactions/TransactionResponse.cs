namespace GroupSplit.Shared;

public record TransactionResponse
{
    public Guid Id { get; set; }
    public ActivityKind Kind { get; set; } = ActivityKind.Expense;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset DateTime { get; set; }

    /// <summary>True when this expense is already linked to an imported bank row.</summary>
    public bool HasBankTransaction { get; set; }

    /// <summary>
    /// The group it was recorded in. A personal expense is in its payer's personal group,
    /// whose id is the payer's own user id. Nullable only for clients written when a
    /// personal expense had no group; the API always sends one now.
    /// </summary>
    public Guid? GroupId { get; set; }

    public string? GroupName { get; set; }

    public Guid PaidByUserId { get; set; }
    public string PaidByUserName { get; set; } = "";

    /// <summary>
    /// True when whoever paid is one of the group's guests. A guest can front money for the
    /// group like anybody else, and the label beside their name is what keeps that from
    /// reading as a member who joined.
    /// </summary>
    public bool PaidByIsGuest { get; set; }

    /// <summary>The name <see cref="PaidByIsGuest"/> had before guests were renamed; still written for older clients.</summary>
    [Obsolete("Use PaidByIsGuest.")]
    public bool PaidByIsPendingInvitee
    {
        get => PaidByIsGuest;
        set => PaidByIsGuest = value;
    }
    public Guid? PaidToUserId { get; set; }
    public string? PaidToUserName { get; set; }
    /// <summary>What it was filed under, or null for an expense filed under nothing.</summary>
    public Guid? CategoryId { get; set; }

    public string? Category { get; set; }

    /// <summary>
    /// Where it was spent, or null for none.
    /// </summary>
    /// <remarks>
    /// The id and not only the name, because the name alone cannot be edited back: a
    /// dialog that reads a transaction and writes it again had no way to say "leave the
    /// place as it is" and no way to show which row the name belonged to, so the field was
    /// writable through the API and the CLI and unreachable from the app.
    /// </remarks>
    public Guid? MerchantId { get; set; }

    /// <summary>
    /// The place's name, for an expense filed from a bank row that named one, or one
    /// somebody chose by hand. See <see cref="GroupActivityResponse.MerchantName"/>.
    /// </summary>
    public string? MerchantName { get; set; }

    /// <summary>The merchant's logo, where there is one.</summary>
    public string? MerchantLogoUrl { get; set; }
}
