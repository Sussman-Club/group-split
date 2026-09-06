namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The CLI's own <c>code</c> slugs for the error envelope, for failures that never reached
/// the API. When a request did reach the API, the envelope carries that response's code
/// from <see cref="GroupSplit.Shared.Errors.ErrorCodes"/> verbatim instead -- those are
/// already the documented contract (see <c>docs/errors.md</c>) and re-mapping them here
/// would give callers two names for one condition.
/// <para>
/// The <c>CLI_</c> prefix is what tells the two apart: a code without it came from the
/// server and means the request was understood and refused.
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

    /// <summary>The server answered, but with nothing this CLI can interpret.</summary>
    public const string ServerError = "CLI_SERVER_ERROR";

    /// <summary>No server URL was configured by flag, environment or config file.</summary>
    public const string ServerNotConfigured = "CLI_SERVER_NOT_CONFIGURED";

    /// <summary>The config file is unreadable.</summary>
    public const string ConfigError = "CLI_CONFIG_ERROR";

    /// <summary>Interrupted, e.g. Ctrl+C.</summary>
    public const string Cancelled = "CLI_CANCELLED";

    /// <summary>An unhandled exception. This one is always a defect in the CLI.</summary>
    public const string Internal = "CLI_INTERNAL_ERROR";
}
