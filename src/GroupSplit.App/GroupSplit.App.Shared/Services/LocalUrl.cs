using System.Diagnostics.CodeAnalysis;

namespace GroupSplit.App.Shared.Services;

/// <summary>
/// Whether a caller-supplied return URL stays on this site, shared by the server that
/// redirects to it and the pages that hand it along so the two cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// The rules are ASP.NET's own <c>IsLocalUrl</c>, which neither side can call: one is a
/// minimal-API endpoint with no <c>IUrlHelper</c>, the other runs in WebAssembly. A path
/// that starts with <c>//</c> or <c>/\</c> is a protocol-relative address on another host.
/// Control characters count against it because browsers strip tabs and newlines out of a
/// URL before parsing it, which turns <c>/&#9;/evil.example</c> into <c>//evil.example</c>
/// after every check here has already passed.
/// </para>
/// <para>
/// The value is checked again once percent-decoded. Anything that decodes it a second time
/// on the way to the <c>Location</c> header would otherwise undo the first check. A real
/// return URL with an encoded control character in its query string lands on <c>/</c>
/// instead, which is the cheaper of the two mistakes.
/// </para>
/// </remarks>
public static class LocalUrl
{
    public static bool IsLocal([NotNullWhen(true)] string? url) =>
        IsLocalPath(url) && IsLocalPath(Uri.UnescapeDataString(url));

    private static bool IsLocalPath([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return false;
        }

        if (url.Length > 1 && url[1] is '/' or '\\')
        {
            return false;
        }

        return !url.Any(char.IsControl);
    }
}
