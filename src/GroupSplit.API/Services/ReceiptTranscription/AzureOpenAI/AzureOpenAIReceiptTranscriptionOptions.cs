namespace GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;

/// <summary>Azure OpenAI receipt transcription settings supplied by the AppHost.</summary>
public sealed class AzureOpenAIReceiptTranscriptionOptions
{
    public const string SectionName = "AzureOpenAI";
    public bool Enabled { get; set; }
}