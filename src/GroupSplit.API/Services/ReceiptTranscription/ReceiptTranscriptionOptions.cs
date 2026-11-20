namespace GroupSplit.API.Services.ReceiptTranscription;

/// <summary>Configuration for receipt transcription.</summary>
public sealed class ReceiptTranscriptionOptions
{
    public const string SectionName = "ReceiptTranscription";

    /// <summary>
    /// How long one reading may take, from sending the file to the last byte of the answer.
    /// Reading a long receipt is slow work on the provider's side, and a person is waiting in
    /// front of a spinner for it, so this is one budget for one attempt: the request is never
    /// sent a second time. A reading that runs out of it answers the retryable 502 and the
    /// person decides whether to ask again.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
