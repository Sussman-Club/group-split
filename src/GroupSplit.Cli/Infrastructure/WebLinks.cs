namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The web app's URLs for the two kinds of link a group hands out.
/// </summary>
/// <remarks>
/// Composed here, from the server origin this client was pointed at, because the URL is the
/// client's to know and the API's tokens are all it sends. Pointed straight at the API and
/// nowhere else, the CLI has no way to know where a browser would find the app, so these are
/// null rather than a guess.
/// </remarks>
internal static class WebLinks
{
    /// <summary>A group's join link: <c>{web}/join/{token}</c>.</summary>
    public static string? Join(CliContext context, string token) => At(context, $"join/{token}");

    /// <summary>A guest's link: <c>{web}/claim/{token}</c>.</summary>
    public static string? Claim(CliContext context, string token) => At(context, $"claim/{token}");

    private static string? At(CliContext context, string path)
    {
        var web = context.Endpoints.WebOrNull;

        return web is null ? null : $"{web.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{path}";
    }
}
