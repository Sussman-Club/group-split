using GroupSplit.Shared;

namespace GroupSplit.API.Test.Receipts;

public sealed class ReceiptTaxesTest
{
    [Fact]
    public void Rates_without_line_tax_calculate_and_reconcile_the_printed_tax()
    {
        var amounts = ReceiptTaxes.Calculate(.75m,
            [new(4m, 0m, 7.5m, null), new(3m, 0m, 7.5m, null), new(3m, 0m, 7.5m, null),
                new(5m, 0m, null, false)]);
        Assert.Equal([.30m, .22m, .23m, 0m], amounts);
        Assert.Equal(.75m, amounts.Sum());
    }

    [Fact]
    public void The_remaining_tax_goes_to_missing_lines_before_changing_a_read_line_tax()
    {
        var amounts = ReceiptTaxes.Calculate(1.50m,
            [new(10m, 0m, 5m, true), new(10m, .25m, null, null), new(10m, 0m, null, true)]);
        Assert.Equal([.50m, .25m, .75m], amounts);
    }

    [Fact]
    public void Even_a_remaining_cent_goes_to_a_missing_line_before_adjusting_known_rates()
    {
        var amounts = ReceiptTaxes.Calculate(.21m,
            [new(1m, 0m, 10m, true), new(1m, 0m, 10m, true), new(1m, 0m, null, true)]);
        Assert.Equal([.10m, .10m, .01m], amounts);
    }

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
    public void A_large_disagreement_does_not_silently_inflate_an_already_read_line_tax()
    {
        var amounts = ReceiptTaxes.Calculate(3m,
            [new(100m, 0m, 1m, true), new(10m, .50m, null, true)]);
        Assert.Equal([1m, .50m], amounts);
    }

    [Fact]
    public void Mixed_rates_discounts_and_exempt_lines_reconcile_deterministically_under_stress()
    {
        var random = new Random(481);
        decimal[] rates = [0m, .5m, 5m, 7.5m, 10m, 20m];
        for (var sample = 0; sample < 500; sample++)
        {
            var items = Enumerable.Range(0, random.Next(2, 151)).Select(index =>
                new ReceiptTaxLine((index % 7 == 0 ? -1 : 1) * random.Next(1, 10000) / 100m,
                    0m, rates[random.Next(rates.Length)], index % 5 != 0)).ToArray();
            var exact = items.Select(item => item.IsTaxable == false ? 0m
                : item.TotalPrice * item.TaxRate!.Value / 100m).ToArray();
            var printedTax = decimal.Round(exact.Sum(), 2, MidpointRounding.AwayFromZero);
            var amounts = ReceiptTaxes.Calculate(printedTax, items);
            Assert.Equal(printedTax, amounts.Sum());
            for (var index = 0; index < items.Length; index++)
            {
                Assert.Equal(amounts[index], decimal.Round(amounts[index], 2));
                if (items[index].IsTaxable == false || items[index].TaxRate == 0)
                    Assert.Equal(0m, amounts[index]);
                else
                    Assert.InRange(Math.Abs(amounts[index] - exact[index]), 0m, .01m);
            }
            Assert.Equal(amounts, ReceiptTaxes.Calculate(printedTax,
                items.Select((item, index) => item with { TaxAmount = amounts[index] }).ToArray()));
        }
    }

    [Fact]
    public void Printed_line_taxes_are_preserved_even_when_the_receipt_disagrees_by_a_cent()
    {
        var amounts = ReceiptTaxes.Calculate(.21m, [new(1m, .11m, null, null), new(1m, .11m, null, null)]);
        Assert.Equal([.11m, .11m], amounts);
    }
}
