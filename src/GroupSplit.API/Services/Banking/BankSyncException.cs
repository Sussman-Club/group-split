namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Why a connector could not answer, in the three ways the sync engine can do something
/// about.
/// </summary>
public enum BankSyncFailure
{
    /// <summary>The token no longer works; the person has to sign in at the bank again.</summary>
    LoginRequired,

    /// <summary>
    /// The provider changed the data under the page run and wants it started over from the
    /// cursor the run began with.
    /// </summary>
    RestartFromCursor,

    /// <summary>The provider or the network had a bad moment. Nothing to do but try later.</summary>
    Transient
}

/// <summary>
/// The one exception a connector may throw. Anything else it meets is a bug, and
/// propagates as one so it is seen rather than retried.
/// </summary>
public sealed class BankSyncException(BankSyncFailure kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public BankSyncFailure Kind { get; } = kind;
}
