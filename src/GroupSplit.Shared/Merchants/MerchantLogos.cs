namespace GroupSplit.Shared;

/// <summary>
/// Where a merchant's logo is allowed to come from: the bank provider's own image hosts, or
/// the marks the app ships itself. Nowhere else.
/// </summary>
/// <remarks>
/// A merchant is shared by every group that has spent there, so its logo is an image every
/// one of their browsers loads. An address anybody could type would make that a tracking
/// pixel on other people's ledgers -- the server behind it learns each viewer's IP and when
/// they looked -- or a way to put any picture at all on them. So the column only ever holds
/// an address from this list, checked when it is written and again when it is rendered:
/// the second check is what covers a row written before the first one existed.
/// <para>
/// In Shared because both ends need the same answer. The API refuses a request that names
/// another host and drops one a provider sends; the app renders initials instead of an
/// image it would not have accepted.
/// </para>
/// </remarks>
public static class MerchantLogos
{
    /// <summary>
    /// Plaid's image hosts: the merchant logos it enriches a row with, and the category
    /// icons it sends on every row. The only provider the sync speaks to today; a second
    /// one adds its host here.
    /// </summary>
    public static readonly IReadOnlySet<string> ProviderHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "plaid-merchant-logos.plaid.com",
        "plaid-category-icons.plaid.com"
    };

    /// <summary>
    /// The marks the app serves itself, out of the shared project's <c>wwwroot/merchants</c>.
    /// Root-relative, so it resolves against whichever client is rendering it.
    /// </summary>
    public const string BundledPath = "/_content/GroupSplit.App.Shared/merchants/";

    /// <summary>
    /// Whether <paramref name="url"/> is somewhere a logo may be loaded from.
    /// </summary>
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var value = url.Trim();

        if (value.StartsWith(BundledPath, StringComparison.Ordinal))
            return IsBundledFile(value[BundledPath.Length..]);

        // Absolute, https, on the default port, with no credentials in it. Each of those is
        // a way for an address to look like the provider's and not be: "http" can be
        // rewritten on the way, a port is a different service on the same name, and
        // "user@host" is read by some people as the user part being the host.
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && uri.IsDefaultPort
               && string.IsNullOrEmpty(uri.UserInfo)
               && ProviderHosts.Contains(uri.Host);
    }

    /// <summary>
    /// <paramref name="url"/> trimmed, when it is allowed; otherwise null, which renders as
    /// initials -- the same as a place no provider had a logo for.
    /// </summary>
    public static string? Usable(string? url) => IsAllowed(url) ? url!.Trim() : null;

    /// <summary>
    /// A file name and nothing else: lower-case words joined by hyphens, ending in
    /// <c>.svg</c>. No slashes, no dots before the extension and no query, so the path
    /// cannot climb out of the folder or name something that is not one of the marks.
    /// </summary>
    private static bool IsBundledFile(string name)
    {
        const string extension = ".svg";

        if (!name.EndsWith(extension, StringComparison.Ordinal) || name.Length == extension.Length)
            return false;

        foreach (var c in name[..^extension.Length])
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return true;
    }
}
