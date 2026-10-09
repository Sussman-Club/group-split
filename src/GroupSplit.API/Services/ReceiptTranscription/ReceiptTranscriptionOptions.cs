namespace GroupSplit.API.Services.ReceiptTranscription;

/// <summary>
/// Selects the receipt transcription provider. An empty value preserves the legacy behavior:
/// configured Veryfi is preferred, then configured Azure OpenAI, otherwise transcription is off.
/// </summary>
public sealed class ReceiptTranscriptionOptions
{
    public const string SectionName = "ReceiptTranscription";

    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// How long one reading may take, from sending the file to the last byte of the answer.
    /// Reading a long receipt is slow work on the provider's side, and a person is waiting in
    /// front of a spinner for it, so this is one budget for one attempt: the request is never
    /// sent a second time. A reading that runs out of it answers the retryable 502 and the
    /// person decides whether to ask again.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
