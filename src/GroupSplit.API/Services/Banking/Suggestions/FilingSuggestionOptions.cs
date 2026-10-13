namespace GroupSplit.API.Services.Banking.Suggestions;

/// <summary>
/// The classifier that suggests a group and category for an imported row.
/// </summary>
/// <remarks>
/// It runs on the Azure resource receipt reading already uses, through its own deployment:
/// Microsoft-Decision-1 answers a multiple-choice question with a probability for each
/// option, which is exactly a discriminator between the person's own groups and
/// categories -- it cannot invent one. The endpoint and key are the receipt connection's;
/// what this adds is which deployment to ask.
/// </remarks>
public sealed class FilingSuggestionOptions
{
    public const string SectionName = "FilingSuggestions";

    /// <summary>
    /// The Decision-1 deployment's name. Empty leaves the feature off, and the inbox
    /// behaves exactly as it did without it.
    /// </summary>
    public string? Deployment { get; set; }

    /// <summary>The Azure resource the deployment is on, without a path.</summary>
    public Uri? Endpoint { get; set; }

    public string? Key { get; set; }

    /// <summary>
    /// Below this the answer is treated as an abstention. A wrong suggestion said with
    /// confidence costs more trust than no suggestion.
    /// </summary>
    public double Threshold { get; set; } = 0.6;

    /// <summary>How many earlier filings of the same merchant to give as evidence.</summary>
    public int HistoryLimit { get; set; } = 10;

    /// <summary>How many rows are asked about at once.</summary>
    public int Concurrency { get; set; } = 5;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Deployment)
        && Endpoint is not null
        && !string.IsNullOrWhiteSpace(Key);
}
