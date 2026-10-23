namespace GroupSplit.Shared;

// Values are persisted in jobs: append kinds without renumbering existing ones.
public enum PushNotificationKind { ExpenseCreated, ExpenseChanged, Settlement, PaymentRequest, InvitationAccepted, BankSyncIssue }
public enum PushNotificationCategory { Expenses, Settlements, GuestsJoining, BankSyncIssues }

public sealed record PushNotificationDefinition(
    PushNotificationKind Kind, PushNotificationCategory Category, string BodyTemplate);

public static class PushNotificationKinds
{
    // The server and native clients share this catalog. More events within an existing
    // category need no additional preference column or UI switch.
    public static IReadOnlyList<PushNotificationDefinition> Definitions { get; } = Array.AsReadOnly<PushNotificationDefinition>(
    [
        new(PushNotificationKind.ExpenseCreated, PushNotificationCategory.Expenses, "An expense was added to {0}."),
        new(PushNotificationKind.ExpenseChanged, PushNotificationCategory.Expenses, "An expense was changed in {0}."),
        new(PushNotificationKind.Settlement, PushNotificationCategory.Settlements, "A settlement was recorded in {0}."),
        new(PushNotificationKind.PaymentRequest, PushNotificationCategory.Settlements, "You have a payment request in {0}."),
        new(PushNotificationKind.InvitationAccepted, PushNotificationCategory.GuestsJoining, "A guest joined {0}."),
        new(PushNotificationKind.BankSyncIssue, PushNotificationCategory.BankSyncIssues, "A linked bank needs your attention.")
    ]);

    public static IReadOnlyList<PushNotificationKind> Settings { get; } = Array.AsReadOnly(
        Definitions.DistinctBy(d => d.Category).Select(d => d.Kind).ToArray());

    private static PushNotificationDefinition Definition(PushNotificationKind kind) =>
        Definitions.FirstOrDefault(d => d.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));

    public static PushNotificationCategory Category(PushNotificationKind kind) => Definition(kind).Category;
    public static string Body(PushNotificationKind kind, string groupName) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Definition(kind).BodyTemplate, groupName);

    public static string Channel(PushNotificationKind kind) => Category(kind) switch
    {
        PushNotificationCategory.Expenses => "group_expenses",
        PushNotificationCategory.Settlements => "settlements",
        PushNotificationCategory.GuestsJoining => "invitations",
        PushNotificationCategory.BankSyncIssues => "bank_sync",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Label(PushNotificationKind kind) => Category(kind) switch
    {
        PushNotificationCategory.Expenses => "Expenses",
        PushNotificationCategory.Settlements => "Settlements",
        PushNotificationCategory.GuestsJoining => "Guests joining",
        PushNotificationCategory.BankSyncIssues => "Bank sync issues",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string Preference(PushNotificationKind kind) => Category(kind) switch
    {
        PushNotificationCategory.Expenses => "push.expenses",
        PushNotificationCategory.Settlements => "push.settlements",
        PushNotificationCategory.GuestsJoining => "push.invitations",
        PushNotificationCategory.BankSyncIssues => "push.bank_sync",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static string? Route(PushNotificationKind kind, Guid groupId, string? requestToken = null)
    {
        if (!Enum.IsDefined(kind)) return null;
        if (kind == PushNotificationKind.BankSyncIssue) return "/account";
        if (kind == PushNotificationKind.PaymentRequest)
            return requestToken is { Length: > 0 and <= 128 }
                && requestToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                ? "/settlement-requests/" + requestToken : null;
        return groupId == Guid.Empty ? null : $"/groups/{groupId}";
    }
}
