using System.Text.Json.Serialization;

namespace GroupSplit.Shared;

[JsonConverter(typeof(JsonStringEnumConverter<ReceiptAdjustmentKind>))]
public enum ReceiptAdjustmentKind
{
    FixedAmount,
    Percentage
}
