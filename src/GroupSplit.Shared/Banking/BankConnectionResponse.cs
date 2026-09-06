namespace GroupSplit.Shared;

/// <summary>
/// Whether a connection can be synced, as the app shows it.
/// </summary>
/// <remarks>
/// Mirrors the stored status. A wire enum rather than the entity's, because
/// <c>GroupSplit.Shared</c> is what the clients compile against and it knows nothing about
/// the database. <c>BankConnectionStateTest</c> keeps the two in step.
/// </remarks>
public enum BankConnectionState
{
    Active,
    LoginRequired,
    Revoked
}

/// <summary>
/// The banks somebody has linked, and whether linking one is possible at all.
/// </summary>
/// <param name="Enabled">
/// False when this deployment has no bank provider configured. The list is then empty and
/// the client says bank sync is off rather than offering a button that cannot work.
/// </param>
public sealed record BankConnectionsResponse(bool Enabled, IReadOnlyList<BankConnectionResponse> Connections);

/// <summary>
/// One linked institution. Never the access token, never the cursor.
/// </summary>
public sealed record BankConnectionResponse(
    Guid Id,
    string Provider,
    string InstitutionName,
    BankConnectionState Status,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<LinkedAccountResponse> Accounts)
{
    /// <summary>
    /// Whether the person has to sign in at their bank again before anything else works.
    /// The linked-banks card leads with this.
    /// </summary>
    public bool NeedsAttention => Status is BankConnectionState.LoginRequired or BankConnectionState.Revoked;
}

public sealed record LinkedAccountResponse(
    Guid Id,
    string Name,
    string? Mask,
    string Type,
    string? Subtype,
    string Currency);
