namespace GroupSplit.App.Services;

public sealed record NativeOptions(Uri ApiBaseAddress, Uri Authority)
{
    public const string ClientId = "native";
    public static readonly Uri Callback = new("groupsplit://auth/callback");

    public static NativeOptions Create(string api, string authority, bool development)
    {
        static Uri Validate(string value, bool development)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && !(development && uri.Scheme == "http"))
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Configure the native API URL and realm authority with absolute HTTPS URLs (HTTP is allowed only in Debug).");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }
        return new(Validate(api, development), Validate(authority, development));
    }

    public Uri Endpoint(string name) => new(Authority, "protocol/openid-connect/" + name);
}
