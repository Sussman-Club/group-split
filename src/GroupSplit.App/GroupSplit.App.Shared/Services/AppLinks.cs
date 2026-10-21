namespace GroupSplit.App.Shared.Services;

/// <summary>The native WebView's origin is local; shared invitations must use the public website.</summary>
public sealed record AppLinks(Uri? PublicOrigin = null)
{
    public int? AndroidBuild { get; init; }
    public bool IsNative => PublicOrigin is not null;
    public string PublicUrl(string browserBase, string path) =>
        new Uri(PublicOrigin ?? new Uri(browserBase), path.TrimStart('/')).AbsoluteUri;
}
