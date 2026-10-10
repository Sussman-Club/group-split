namespace GroupSplit.App.Shared.Services;

/// <summary>
/// The header the app's own API client sends on every call through the web app's
/// <c>/api</c> route, as proof that one of its own pages sent it.
/// </summary>
/// <remarks>
/// That route is authenticated by a cookie the browser attaches by itself, so it refuses a
/// write unless something shows this origin's own pages made it. <c>Sec-Fetch-Site:
/// same-origin</c> shows that in every current browser. This header shows it in one too old
/// to send <c>Sec-Fetch-Site</c>: a page on another origin cannot add a custom header to a
/// request here without a CORS preflight, and this app answers none. The value carries no
/// meaning; being present is the proof.
/// </remarks>
public static class SameOriginRequest
{
    public const string HeaderName = "X-GroupSplit-Request";

    public const string HeaderValue = "1";
}
