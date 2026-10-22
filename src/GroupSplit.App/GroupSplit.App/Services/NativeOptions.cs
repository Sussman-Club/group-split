using System.Reflection;

namespace GroupSplit.App.Services;

public sealed record NativeOptions(Uri ApiBaseAddress, Uri Authority)
{
    public int? AndroidBuild { get; init; }
    public Uri WebBaseAddress { get; init; } = new("https://groupsplit.sussman.win/");
    public const string ClientId = "native";
    public static readonly Uri Callback = new("groupsplit://auth/callback");

    public static NativeOptions FromAssembly(Assembly assembly, bool development)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        return Create(
            metadata.FirstOrDefault(item => item.Key == "NativeApiUrl")?.Value ?? "",
            metadata.FirstOrDefault(item => item.Key == "NativeAuthority")?.Value ?? "",
            development,
            metadata.FirstOrDefault(item => item.Key == "NativeWebUrl")?.Value) with
        {
            AndroidBuild = int.TryParse(metadata.FirstOrDefault(item => item.Key == "NativeAndroidBuild")?.Value,
                out var build) && build > 0 ? build : null
        };
    }

    public static NativeOptions Create(string api, string authority, bool development, string? web = null)
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
        return new(Validate(api, development), Validate(authority, development))
        {
            WebBaseAddress = Validate(web ?? "https://groupsplit.sussman.win/", development)
        };
    }

    public Uri Endpoint(string name) => new(Authority, "protocol/openid-connect/" + name);
}
