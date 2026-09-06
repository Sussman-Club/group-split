namespace GroupSplit.API.Services.Banking;

/// <summary>
/// Which provider this deployment links banks through.
/// </summary>
/// <remarks>
/// There is no <c>Enabled</c> flag here on purpose. Whether bank sync works is whether a
/// connector is registered for <see cref="Provider"/>, and that registration is what the
/// deployment's <c>plaid-enabled</c> switch controls. A second flag could disagree with the
/// first, and the pair would then have to be kept in step by whoever remembered.
/// </remarks>
public sealed class BankingOptions
{
    public const string SectionName = "Banking";

    /// <summary>
    /// The key of the connector to link new banks through. Existing connections are synced
    /// through whichever provider they were made with, so changing this does not strand
    /// them.
    /// </summary>
    public string Provider { get; set; } = "plaid";
}
