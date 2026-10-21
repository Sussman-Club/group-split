namespace GroupSplit.App.Services;

/// <summary>Keep an invitation received before the WebView is ready, without accepting arbitrary navigation.</summary>
public sealed class NativeLinkRouter(NativeOptions options)
{
    private readonly object gate = new();
    private string? pending;
    public event Action<string>? Changed;

    public bool Open(Uri uri)
    {
        var route = Route(uri, options.WebBaseAddress);
        if (route is null) return false;
        lock (gate) pending = route;
        Changed?.Invoke(route);
        return true;
    }

    public string? Take()
    {
        lock (gate)
        {
            var route = pending;
            pending = null;
            return route;
        }
    }

    public static string? Route(Uri uri, Uri website)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var isWebsite = uri.Scheme == website.Scheme && uri.Authority == website.Authority;
        var isScheme = uri.Scheme == "groupsplit" && uri.Host == "invite" && uri.IsDefaultPort;
        if (!isWebsite && !isScheme) return null;
        var path = uri.AbsolutePath;
        var prefix = isWebsite ? website.AbsolutePath.TrimEnd('/') + "/" : "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = path[prefix.Length..].Split('/');
        if (parts.Length != 2 || parts[0] is not ("join" or "claim") || parts[1].Length is < 1 or > 256
            || parts[1].Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-'))) return null;
        return "/" + string.Join('/', parts);
    }
}
