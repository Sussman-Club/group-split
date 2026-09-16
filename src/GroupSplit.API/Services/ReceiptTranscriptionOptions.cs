namespace GroupSplit.API.Services;

/// <summary>
/// Selects the receipt transcription provider. An empty value preserves the legacy behavior:
/// configured Veryfi is preferred, then configured Azure OpenAI, otherwise transcription is off.
/// </summary>
public sealed class ReceiptTranscriptionOptions
{
    public const string SectionName = "ReceiptTranscription";

    public string Provider { get; set; } = string.Empty;
}
