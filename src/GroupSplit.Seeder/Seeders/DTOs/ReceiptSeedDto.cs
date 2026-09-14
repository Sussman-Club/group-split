using GroupSplit.Shared;
namespace GroupSplit.Seeder.Seeders.DTOs;

public class ReceiptSeedDto
{
    public decimal Tax { get; init; }
    public decimal Tip { get; init; }
    public required IReadOnlyList<ReceiptItemSeedDto> Items { get; init; }
}
public class ReceiptItemSeedDto
{
    public required string Name { get; init; }
    public required decimal Price { get; init; }
    public decimal Quantity { get; init; } = 1;
    public decimal TaxAmount { get; init; }
    public string? RuleName { get; init; }
    public SplitRuleDto? SplitRule { get; init; }
}
