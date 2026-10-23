namespace GroupSplit.Shared;

public enum PushNotificationKind { ExpenseCreated, ExpenseChanged, Settlement, PaymentRequest, InvitationAccepted, BankSyncIssue }

public static class PushNotificationKinds
{
    public static string Channel(PushNotificationKind kind) => kind switch
    {
        PushNotificationKind.ExpenseCreated => "group_expenses",
        PushNotificationKind.ExpenseChanged => "expense_changes",
        PushNotificationKind.Settlement => "settlements",
        PushNotificationKind.PaymentRequest => "payment_requests",
        PushNotificationKind.InvitationAccepted => "invitations",
        PushNotificationKind.BankSyncIssue => "bank_sync",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static string Label(PushNotificationKind kind) => kind switch
    {
        PushNotificationKind.ExpenseCreated => "Expenses",
        PushNotificationKind.ExpenseChanged => "Expense changes",
        PushNotificationKind.Settlement => "Settlements",
        PushNotificationKind.PaymentRequest => "Payment requests",
        PushNotificationKind.InvitationAccepted => "Guests joining",
        PushNotificationKind.BankSyncIssue => "Bank sync issues",
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

    public static string Preference(PushNotificationKind kind) => kind is PushNotificationKind.ExpenseCreated or PushNotificationKind.ExpenseChanged
        ? "push.expenses" : kind is PushNotificationKind.Settlement or PushNotificationKind.PaymentRequest
            ? "push.settlements" : "push." + Channel(kind);
}
