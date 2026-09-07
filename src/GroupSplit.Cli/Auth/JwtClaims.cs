using System.Text.Json;

namespace GroupSplit.Cli.Auth;

/// <summary>
/// Reads the payload of a JWT without verifying it. Only ever used to label what is on
/// screen -- "signed in as ..." -- never to make an access decision. The API validates
/// the signature, and it is the only party in a position to.
/// </summary>
public static class JwtClaims
{
    public static (string? Username, string? Subject, DateTimeOffset? ExpiresAt) Read(string token)
    {
        var parts = token.Split('.');

        if (parts.Length < 2)
        {
            return (null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = document.RootElement;

            var username = TryGetString(root, "preferred_username")
                           ?? TryGetString(root, "email")
                           ?? TryGetString(root, "name");

            DateTimeOffset? expires = root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

            return (username, TryGetString(root, "sub"), expires);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return (null, null, null);
        }
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
