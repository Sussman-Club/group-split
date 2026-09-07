using System.Text.Json.Serialization;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// The error envelope, written to stderr as JSON in <c>--output json</c> and as prose in
/// text mode. <see cref="Remediation"/> is not decoration: it is the field that lets a
/// caller recover without a human reading the message, so every error should carry one
/// when a next step exists.
/// </summary>
public sealed record CliError(
    [property: JsonPropertyName("error")] string Message,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("remediation")] string? Remediation = null,
    [property: JsonPropertyName("details")] IReadOnlyList<string>? Details = null)
{
    /// <summary>
    /// The API's trace id, when the failure came from a request. Present so a report of a
    /// failure can be matched to the server's own log line without asking for a repro.
    /// </summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }
}

/// <summary>
/// A failure that is the user's or the server's, not a bug. These are reported as the
/// error envelope and the mapped exit code; anything else that escapes to the top is a
/// defect and is reported as such.
/// </summary>
public sealed class CliException(CliError error, int exitCode, Exception? inner = null)
    : Exception(error.Message, inner)
{
    public CliError Error { get; } = error;

    public int ExitCode { get; } = exitCode;

    public static CliException Auth(string message, string? remediation = null, string code = ErrorCodes.AuthRequired)
        => new(new CliError(message, code, remediation), ExitCodes.AuthRequired);

    public static CliException Input(string message, string? remediation = null,
        IReadOnlyList<string>? details = null, string code = ErrorCodes.InvalidInput)
        => new(new CliError(message, code, remediation, details), ExitCodes.InvalidInput);

    public static CliException Failure(string message, string code = ErrorCodes.ServerError,
        string? remediation = null, IReadOnlyList<string>? details = null, Exception? inner = null)
        => new(new CliError(message, code, remediation, details), ExitCodes.Error, inner);
}
