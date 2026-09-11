namespace GroupSplit.Shared;

public record TransactionResponse
{
    public Guid Id { get; set; }
    public ActivityKind Kind { get; set; } = ActivityKind.Expense;
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset DateTime { get; set; }

    /// <summary>
    /// The group it was recorded in, or null when it is the caller's own. Null is not a
    /// missing value: an expense with no group is what personal means, and the hidden
    /// group that used to stand in for it was the reason "Personal · 1" appeared in the
    /// group switcher.
    /// </summary>
    public Guid? GroupId { get; set; }

    public string? GroupName { get; set; }

    public Guid PaidByUserId { get; set; }
    public string PaidByUserName { get; set; } = "";

    /// <summary>
    /// True when whoever paid has been invited to the group and has not joined yet. An
    /// invitee can front money for the group from the moment they are asked, and the label
    /// beside their name is what keeps that from reading as a member who joined.
    /// </summary>
    public bool PaidByIsPendingInvitee { get; set; }
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
