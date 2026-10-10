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
    /// <para>
    /// Written at Debug even when on, so turning this on is not enough by itself: the
    /// provider's category has to be let through at Debug as well
    /// (<c>Logging__LogLevel__GroupSplit.API.Services.ReceiptTranscription.Veryfi=Debug</c>).
    /// At Information it went to the OTLP exporter with everything else, and a flag set once
    /// for one investigation shipped every receipt after it.
    /// </para>
    /// </summary>
    public bool LogRawResponses { get; set; }
}