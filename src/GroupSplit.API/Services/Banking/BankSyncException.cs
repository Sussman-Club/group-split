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

    /// <summary>
    /// The provider has no item for the token it was handed: removed at the provider's end,
    /// or already removed by us on an earlier attempt.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Kind"/> rather than a kind of its own, because for everything but
    /// one caller it means what <see cref="BankSyncFailure.LoginRequired"/> means -- the
    /// connection needs the person. The exception is unlinking, where it is the outcome
    /// being asked for: an item the provider does not know is an item already handed back.
    /// </remarks>
    public bool ItemUnknown { get; init; }
}
