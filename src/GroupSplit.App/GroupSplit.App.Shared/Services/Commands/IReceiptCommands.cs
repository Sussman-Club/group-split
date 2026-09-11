using GroupSplit.Shared;

namespace GroupSplit.App.Shared.Services.Commands;

/// <summary>
/// The itemised bill behind an expense, for the screens that show one.
/// </summary>
/// <remarks>
/// Reading only, for now: the app shows a bill that is already there, and typing one up is
/// the CLI's job. Which is why there is no presenter here and nothing announces -- see
/// <see cref="GetAsync"/> for why a failure is silent.
/// </remarks>
public interface IReceiptCommands
{
    /// <summary>
    /// The bill on that expense, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Null rather than a refusal, and silent rather than announced, because <em>most
    /// expenses have no bill</em>. A receipt is the exception -- a shared dinner somebody
    /// itemised -- so a missing one is the ordinary case and not a failure: the section
    /// simply does not appear. Announcing it would put a snackbar on every expense anybody
    /// opened.
    /// <para>
    /// A genuine failure -- the network, a 500 -- is folded into the same null on purpose.
    /// The bill is supplementary to a dialog whose subject loaded fine, and the alternative
    /// is interrupting somebody reading an expense to tell them about a section they did not
    /// ask for.
    /// </para>
    /// </remarks>
    Task<ReceiptResponse?> GetAsync(Guid transactionId, CancellationToken ct = default);
}
