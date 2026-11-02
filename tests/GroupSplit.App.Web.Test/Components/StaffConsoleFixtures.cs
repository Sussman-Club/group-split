using GroupSplit.Shared;

namespace GroupSplit.App.Web.Test.Components;

internal static class StaffConsoleFixtures
{
    public static List<SupportTicketResponse> Tickets()
    {
        var now = DateTimeOffset.UtcNow;
        string[] titles = ["Bank sync is missing recent expenses", "Cannot accept a group invitation", "Add a monthly spending export", "Receipt total differs from the bill", "How do I split a shared subscription?", "Merchant logo needs updating"];
        return titles.Select((title, i) => new SupportTicketResponse(Guid.NewGuid(), Guid.NewGuid(), null,
            i == 2 ? "FeatureRequest" : i == 4 ? "Support" : "Bug", "New", i < 2 ? "High" : "Normal", i != 1, title,
            "My bank is connected, but the last three card payments have not appeared in the inbox. I tried refreshing and signing in again. Earlier transactions are still visible.",
            "Open Inbox, choose the linked bank, then refresh transactions.", now.AddHours(-i - 1), now.AddMinutes(-i * 21 - 8))).ToList();
    }

    public static SupportTicketSummary Summary(SupportTicketResponse ticket) => new(ticket.Id, ticket.ReporterUserId, ticket.AssignedUserId, ticket.Type, ticket.Status, ticket.Priority, ticket.ClassificationNeedsReview, ticket.Title, ticket.CreatedAt, ticket.UpdatedAt) { ReporterName = ticket.ReporterName, AssignedUserName = ticket.AssignedUserName };

    public static List<MerchantResponse> Merchants() =>
    [
        new(Guid.NewGuid(), "Blue Bottle Coffee", MerchantLogos.BundledPath + "blue-bottle-coffee.svg", DateTimeOffset.UtcNow.AddMonths(-3), 42),
        new(Guid.NewGuid(), "Corner Market", null, DateTimeOffset.UtcNow.AddMonths(-2), 18),
        new(Guid.NewGuid(), "Lidl", null, DateTimeOffset.UtcNow.AddMonths(-6), 126),
        new(Guid.NewGuid(), "Metro Transit", null, DateTimeOffset.UtcNow.AddMonths(-4), 67),
        new(Guid.NewGuid(), "Whole Foods Market", null, DateTimeOffset.UtcNow.AddMonths(-5), 83)
    ];

    public static void Export(string markup, string view)
    {
        var output = Environment.GetEnvironmentVariable("GROUPSPLIT_STAFF_PREVIEW");
        if (string.IsNullOrEmpty(output)) return;
        output = Path.Combine(Path.GetDirectoryName(output)!, $"staff-{view}.html");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "group-split.slnx"))) root = root.Parent;
        var cssRoot = Path.Combine(root!.FullName, "src/GroupSplit.App/GroupSplit.App.Shared/wwwroot/css");
        var css = string.Join("\n", new[] { "tokens.css", "base.css", "layout.css", "components/cards.css", "components/avatars.css", "mudblazor.css", "components/staff.css" }
            .Select(file => File.ReadAllText(Path.Combine(cssRoot, file))));
        var mudCss = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages/mudblazor/9.9.0/staticwebassets/MudBlazor.min.css");
        if (File.Exists(mudCss)) css = File.ReadAllText(mudCss) + "\n" + css;
        File.WriteAllText(output, "<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><style>" + css
            + "body{margin:0;font-family:var(--gs-font-body);color:var(--gs-ink)}.gs-brand{display:flex;align-items:center;text-decoration:none;color:inherit}.gs-card{background:var(--gs-surface);border:1px solid var(--gs-line);border-radius:var(--gs-radius-lg);box-shadow:var(--gs-shadow-sm)}</style>"
            + markup + "</html>");
    }
}
