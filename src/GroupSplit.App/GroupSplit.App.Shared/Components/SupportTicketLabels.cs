namespace GroupSplit.App.Shared.Components;

internal static class SupportTicketLabels
{
    public static readonly string[] Statuses = ["New", "UnderReview", "InProgress", "WaitingForCustomer", "Resolved", "Closed"];
    public static readonly string[] Types = ["Bug", "FeatureRequest", "Support"];
    public static readonly string[] Priorities = ["Low", "Normal", "High", "Urgent"];
    public static string Key(string value) => value switch
    {
        "UnderReview" => "Under review",
        "InProgress" => "In progress",
        "WaitingForCustomer" => "Waiting for customer",
        "FeatureRequest" => "Feature request",
        _ => value
    };
}
