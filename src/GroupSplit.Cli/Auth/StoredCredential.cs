using System.Text.Json.Serialization;

namespace GroupSplit.Cli.Auth;

public sealed class StoredCredential
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Shown by `auth status`. Informational only -- the API decides who you are.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>
    /// A minute of slack, so a token that would expire mid-request is treated as already
    /// expired. The slack is added to now rather than subtracted from the expiry, which
    /// would overflow for a credential whose ExpiresAt was never set.
    /// </summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(60);
}

public sealed class CredentialFile
{
    [JsonPropertyName("credentials")]
    public Dictionary<string, StoredCredential> Credentials { get; set; } = new(StringComparer.Ordinal);
}
