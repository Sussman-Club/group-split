using System.Text.Json.Serialization;

namespace GroupSplit.Cli.Output;

/// <summary>
/// What a mutation writes to stdout when it was not confirmed, alongside exit code 4.
/// <para>
/// The point of <see cref="ConfirmCommand"/> is that a caller with no terminal -- CI, or
/// an agent -- does not have to reconstruct the invocation from its own history to
/// proceed. It re-runs the string verbatim, or shows it to a human and asks.
/// </para>
/// </summary>
public sealed record ConfirmationRequest(
    [property: JsonPropertyName("confirmationRequired")] bool ConfirmationRequired,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("changes")] IReadOnlyList<string> Changes,
    [property: JsonPropertyName("confirmCommand")] string ConfirmCommand)
{
    public ConfirmationRequest(string action, string summary, IReadOnlyList<string> changes, string confirmCommand)
        : this(true, action, summary, changes, confirmCommand)
    {
    }
}
