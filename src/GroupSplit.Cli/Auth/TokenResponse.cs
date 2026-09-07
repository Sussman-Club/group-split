using System.Text.Json.Serialization;

namespace GroupSplit.Cli.Auth;

public sealed record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }
}

/// <summary>The device authorization response of RFC 8628 section 3.2.</summary>
public sealed record DeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = string.Empty;

    /// <summary>The URI with the code already in it, so a human need not retype it.</summary>
    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; init; }

    /// <summary>
    /// Seconds the code stays valid. Nullable so an absent value and an explicit zero stay
    /// distinguishable: absent takes a default, zero means the code is already dead.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; init; }

    /// <summary>Seconds the server wants between polls. Absent means 5, per the RFC.</summary>
    [JsonPropertyName("interval")]
    public int? Interval { get; init; }
}
