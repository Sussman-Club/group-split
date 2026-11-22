using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

/// <summary>
/// How a transaction came to be recorded.
/// </summary>
/// <remarks>
/// Set once, when the transaction is written, and never changed by an edit or by linking a
/// bank row to it afterwards: it answers "how did this get here", not "what is it attached
/// to now". <see cref="TransactionResponse.HasBankTransaction"/> answers the second.
/// <para>
/// <see cref="Mcp"/> wins over the others. An assistant filing a bank row records
/// <see cref="Mcp"/>, because that it was an assistant is the one fact nothing else on the
/// row keeps; the bank row it was filed from is still linked.
/// </para>
/// <para>
/// It travels and is stored as its name, as <see cref="InboxStatus"/> does, so reordering the
/// members cannot change what an older row or an older client means.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<TransactionSource>))]
public enum TransactionSource
{
    /// <summary>Typed in by somebody in the app: an expense, or a repayment.</summary>
    Manual,

    /// <summary>Filed from an imported bank row.</summary>
    Bank,

    /// <summary>Filed from a receipt in the receipt inbox.</summary>
    Receipt,

    /// <summary>Recorded through the MCP server, by an assistant acting for somebody.</summary>
    Mcp
}
