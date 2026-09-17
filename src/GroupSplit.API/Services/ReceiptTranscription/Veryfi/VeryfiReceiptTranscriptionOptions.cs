namespace GroupSplit.API.Services.ReceiptTranscription.Veryfi;

/// <summary>Veryfi's credentials and endpoint, kept at the provider boundary.</summary>
public sealed class VeryfiReceiptTranscriptionOptions
{
    public const string SectionName = "Veryfi";

    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Uri Endpoint { get; set; } = new("https://api.veryfi.com/");

    /// <summary>
    /// Writes the provider's whole response to the log, for working out why one receipt came
    /// back wrong. Off by default: the body is a person's shopping, and the log is a wider
    /// audience than the expense the file was attached to.
    /// </summary>
    public bool LogRawResponses { get; set; }
}