namespace GroupSplit.App.Shared.Components;

/// <summary>Shareable queue URLs and validated return destinations.</summary>
internal static class StaffLinks
{
    public static string Tickets(string? search, string? status, string? priority, bool review, int page, string? assignee = null, string? sort = null, string? type = null) =>
        Build("/staff/tickets", ("q", search), ("status", status ?? "New"), ("priority", priority),
            ("assignee", assignee), ("sort", sort), ("type", type), ("review", review ? "true" : null), ("page", page > 0 ? page.ToString() : null));

    public static string Merchants(string? search, int page, string? sort = null) =>
        Build("/staff/merchants", ("q", search), ("sort", sort), ("page", page > 0 ? page.ToString() : null));

    public static string Detail(string path, string returnTo) => path + "?returnTo=" + Uri.EscapeDataString(returnTo);

    public static string ReturnTo(string? requested, string listPath) =>
        requested == listPath || requested?.StartsWith(listPath + "?", StringComparison.Ordinal) == true
            ? requested : listPath;

    private static string Build(string path, params (string Key, string? Value)[] values)
    {
        var query = string.Join("&", values.Where(v => v.Value is not null)
            .Select(v => Uri.EscapeDataString(v.Key) + "=" + Uri.EscapeDataString(v.Value!)));
        return query.Length == 0 ? path : path + "?" + query;
    }
}
