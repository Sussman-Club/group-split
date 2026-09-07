using GroupSplit.Cli.Output;

namespace GroupSplit.Cli.Infrastructure;

/// <summary>
/// Thrown by <see cref="Confirmation"/> when a mutation cannot be confirmed here and now.
/// The runner turns it into the stdout envelope and exit code 4.
/// </summary>
public sealed class ConfirmationRequiredException(ConfirmationRequest request) : Exception(request.Summary)
{
    public ConfirmationRequest Request { get; } = request;
}
