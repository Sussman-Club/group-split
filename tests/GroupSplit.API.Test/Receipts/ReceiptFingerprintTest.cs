using System.Security.Cryptography;
using System.Text;
using GroupSplit.API.Services.ReceiptTranscription.AzureOpenAI;

namespace GroupSplit.API.Test.Receipts;

public sealed class ReceiptFingerprintTest
{
    [Fact]
    public void Warehouse_identity_matches_the_extension_and_requires_every_printed_field()
    {
        var reading = new AzureOpenAIReceiptTranscriptionProvider.AzureReceipt
        {
            IdentityKind = "costco-warehouse",
            WarehouseDate = "2026-11-05",
            WarehouseTime = "14:23",
            WarehouseNumber = "00123",
            TerminalNumber = "04",
            TransactionNumber = "00567"
        };
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "groupsplit:costco-warehouse:v1:2026-11-05:14:23:123:4:567")));
        Assert.Equal(expected, AzureOpenAIReceiptTranscriptionProvider.ToReceipt(reading).ObservedFingerprint);
        var incomplete = new AzureOpenAIReceiptTranscriptionProvider.AzureReceipt
        {
            IdentityKind = "costco-warehouse",
            WarehouseDate = "2026-11-05",
            WarehouseTime = "14:23",
            WarehouseNumber = "123",
            TerminalNumber = "4"
        };
        Assert.Null(AzureOpenAIReceiptTranscriptionProvider.ToReceipt(incomplete).ObservedFingerprint);
    }

    [Theory]
    [InlineData("amazon", "123-1234567-1234567")]
    [InlineData("costco-online", "12345678")]
    public void Online_identities_match_the_extension(string kind, string identity)
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"groupsplit:{kind}:v1:{identity}")));
        var receipt = AzureOpenAIReceiptTranscriptionProvider.ToReceipt(new() { IdentityKind = kind, IdentityValue = identity });
        Assert.Equal(expected, receipt.ObservedFingerprint);
    }

    [Theory]
    [InlineData("amazon", "123")]
    [InlineData("costco-online", "12-34")]
    [InlineData("other", "12345678")]
    public void Ambiguous_identities_are_not_used(string kind, string identity)
    {
        Assert.Null(AzureOpenAIReceiptTranscriptionProvider.ToReceipt(new() { IdentityKind = kind, IdentityValue = identity }).ObservedFingerprint);
    }
}
