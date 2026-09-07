namespace GroupSplit.Shared;

/// <summary>
/// Asks for a token that opens the provider's own linking UI.
/// </summary>
/// <remarks>
/// With <see cref="ConnectionId"/> set the token opens that connection in update mode, so
/// somebody whose bank wants a fresh sign-in repairs what they have instead of linking a
/// second copy of it.
/// </remarks>
public record LinkTokenRequest
{
    public Guid? ConnectionId { get; set; }
}

/// <summary>
/// A short-lived token for the provider's linking UI, and when it stops working.
/// </summary>
public sealed record LinkTokenResponse(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// What the provider's linking UI handed back, to be exchanged for a lasting connection.
/// </summary>
public record CreateBankConnectionRequest
{
    /// <summary>
    /// The provider's one-time token. Not a secret worth protecting for long: it is useless
    /// without this application's credentials and expires in minutes.
    /// </summary>
    public string PublicToken { get; set; } = null!;
}
