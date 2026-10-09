namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The CLI's own <c>code</c> slugs for the error envelope, for failures the API did not
/// answer. When the API did answer, the envelope carries that response's code
/// from <see cref="GroupSplit.Shared.Errors.ErrorCodes"/> verbatim instead -- those are
/// already the documented contract (see <c>docs/errors.md</c>) and re-mapping them here
/// would give callers two names for one condition.
/// <para>
/// The <c>CLI_</c> prefix is what tells the two apart: a code without it came from the
/// server and means the request was understood and refused. A code with it does not
/// promise the request never left -- <see cref="OutcomeUnknown"/> is the one that says it
/// may have, and <see cref="Cancelled"/> can interrupt a request in flight.
/// </para>
/// </summary>
public static class ErrorCodes
{
    /// <summary>No credentials at all. Sign in, or set GROUPSPLIT_TOKEN.</summary>
    public const string AuthRequired = "CLI_AUTH_REQUIRED";

    /// <summary>Credentials existed but are past their expiry and could not be refreshed.</summary>
    public const string AuthExpired = "CLI_AUTH_EXPIRED";

    /// <summary>The identity server refused to issue a token.</summary>
    public const string AuthFailed = "CLI_AUTH_FAILED";

    /// <summary>The arguments were rejected before any request was made.</summary>
    public const string InvalidInput = "CLI_INVALID_INPUT";

    /// <summary>The command line did not parse.</summary>
    public const string Usage = "CLI_USAGE";

    /// <summary>DNS, TLS or connection failure. Nothing was sent.</summary>
    public const string ServerUnreachable = "CLI_SERVER_UNREACHABLE";

    /// <summary>
    /// The request may have reached the server and no answer came back: the connection broke
    /// after it was sent, or the server took too long. Whether it was carried out is unknown.
    /// </summary>
    public const string OutcomeUnknown = "CLI_OUTCOME_UNKNOWN";

    /// <summary>The server answered, but with nothing this CLI can interpret.</summary>
    public const string ServerError = "CLI_SERVER_ERROR";

    /// <summary>No server URL was configured by flag, environment or config file.</summary>
    public const string ServerNotConfigured = "CLI_SERVER_NOT_CONFIGURED";

    /// <summary>The config file is unreadable.</summary>
    public const string ConfigError = "CLI_CONFIG_ERROR";

    /// <summary>
    /// Interrupted, e.g. Ctrl+C, or a confirmation declined. An interrupt that landed while a
    /// change was in flight may not have stopped it.
    /// </summary>
    public const string Cancelled = "CLI_CANCELLED";

    /// <summary>An unhandled exception. This one is always a defect in the CLI.</summary>
    public const string Internal = "CLI_INTERNAL_ERROR";
}
