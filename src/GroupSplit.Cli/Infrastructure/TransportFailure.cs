namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Says what a request that got no answer means for the caller: that it never left, or that
/// nobody can tell whether it was carried out.
/// </summary>
/// <remarks>
/// The two used to be one code, documented as "nothing was sent". That is true of a name that
/// would not resolve or a port that refused the connection, and false of a connection reset
/// after the request went out, or of a server that took longer than the client would wait.
/// An agent told the create never happened retries it, and the group gets the expense twice.
/// </remarks>
public static class TransportFailure
{
    /// <summary>
    /// What fails before a byte of the request is written: finding the host, connecting to
    /// it, agreeing TLS with it, or getting a proxy to open a tunnel to it. Everything else
    /// can happen after the request went out, so it is reported as an unknown outcome.
    /// </summary>
    private static readonly HashSet<HttpRequestError> BeforeSending =
    [
        HttpRequestError.NameResolutionError,
        HttpRequestError.ConnectionError,
        HttpRequestError.SecureConnectionError,
        HttpRequestError.ProxyTunnelError
    ];

    public static CliError From(HttpRequestException exception) =>
        BeforeSending.Contains(exception.HttpRequestError)
            ? new CliError(
                $"Could not reach the server: {exception.Message}",
                ErrorCodes.ServerUnreachable,
                "Check the server URL with: groupsplit config list")
            : OutcomeUnknown($"The connection to the server failed before it answered: {exception.Message}");

    /// <summary>The client gave up waiting. The server may still have done it.</summary>
    public static CliError TimedOut() =>
        OutcomeUnknown("The server did not answer in time.");

    private static CliError OutcomeUnknown(string message) => new(
        message,
        ErrorCodes.OutcomeUnknown,
        "The request may have been carried out. A read is safe to repeat; before repeating a "
        + "change, look for it (list or show what it would have changed), or it may be recorded twice.");
}
