using GroupSplit.Shared;

namespace GroupSplit.API.Test.Receipts;

public sealed class ReceiptTaxesTest
{
    [Fact]
    public void Unidentified_tax_can_be_explicitly_distributed_without_taxing_exempt_items()
    {
        var amounts = ReceiptTaxes.Calculate(.75m,
            [new(4m, 0m, null, null), new(3m, 0m, null, null), new(3m, 0m, null, false)], allocateUnidentified: true);
        Assert.Equal([.43m, .32m, 0m], amounts);
        Assert.Equal(.75m, amounts.Sum());
    }

    [Theory]
    [InlineData(0.21, 0.10, 0.11)]
    [InlineData(0.23, 0.12, 0.11)]
    public void Rounding_cents_are_reconciled_in_printed_order(decimal tax, decimal first, decimal second)
    {
        var amounts = ReceiptTaxes.Calculate(tax,
        [new(1.05m, 0m, 10m, true), new(1.05m, 0m, 10m, true), new(5m, 0m, null, false)]);
        Assert.Equal([first, second, 0m], amounts);
        Assert.Equal(tax, amounts.Sum());
    }

    [Fact]
    public void Larger_rate_disagreements_remain_visible()
    {
        var amounts = ReceiptTaxes.Calculate(1.50m, [new(10m, 0m, 10m, true)]);
        Assert.Equal(1m, Assert.Single(amounts));
    }

    [Fact]
    public void A_negative_rounding_cent_does_not_require_a_discount_line()
    {
        var amounts = ReceiptTaxes.Calculate(.21m, [new(1m, .11m, null, null), new(1m, .11m, null, null)]);
        Assert.Equal(.21m, amounts.Sum());
    }
}
