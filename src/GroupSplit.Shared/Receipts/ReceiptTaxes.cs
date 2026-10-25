namespace GroupSplit.Shared;

public readonly record struct ReceiptTaxLine(decimal TotalPrice, decimal TaxAmount, decimal? TaxRate, bool? IsTaxable);

public static class ReceiptTaxes
{
    public static decimal[] Calculate(decimal totalTax, IReadOnlyList<ReceiptTaxLine> items, bool allocateUnidentified = false)
    {
        var exact = items.Select(item => item.IsTaxable == false ? 0m
            : item.TaxRate is { } rate ? item.TotalPrice * rate / 100m : item.TaxAmount).ToArray();
        var taxes = exact.Select(value => decimal.Round(value, 2, MidpointRounding.AwayFromZero)).ToArray();
        var remaining = totalTax - taxes.Sum();
        if (remaining == 0)
            return taxes;

        var rated = Enumerable.Range(0, items.Count)
            .Where(index => items[index].IsTaxable != false && items[index].TaxRate is > 0
                && items[index].TotalPrice != 0).ToArray();
        var manual = Enumerable.Range(0, items.Count)
            .Where(index => items[index].IsTaxable != false && items[index].TaxRate is null
                && items[index].TotalPrice != 0).ToArray();
        var missing = manual.Where(index => items[index].TaxAmount == 0m && (remaining > 0
            ? items[index].TotalPrice > 0 : items[index].TotalPrice < 0)).ToArray();
        var fillMissing = missing.Length > 0 && (allocateUnidentified
            || items.Any(item => item.TaxRate is not null || item.IsTaxable is not null));
        // Correct cent rounding without hiding a materially incorrect receipt reading.
        var candidates = rated.Length > 0 ? rated : manual;
        if (!fillMissing && Math.Abs(remaining) <= .005m * candidates.Length)
        {
            var direction = Math.Sign(remaining);
            var cents = (int)(Math.Abs(remaining) * 100m);
            foreach (var index in candidates
                .OrderByDescending(index => direction * (exact[index] - taxes[index]))
                .ThenBy(index => index).Take(cents))
                taxes[index] += direction * .01m;
            return taxes;
        }

        if (!allocateUnidentified && items.All(item => item.TaxRate is null && item.IsTaxable is null))
            return taxes;

        // Unknown taxable lines can carry tax not otherwise assigned by the reading.
        var fallback = fillMissing ? missing : manual.Where(index => remaining > 0
            ? items[index].TotalPrice > 0 : items[index].TotalPrice < 0).ToArray();
        var weight = fallback.Sum(index => Math.Abs(items[index].TotalPrice));
        if (weight == 0)
            return taxes;
        var raw = fallback.Select(index => Math.Abs(remaining) * 100m * Math.Abs(items[index].TotalPrice) / weight).ToArray();
        var shares = raw.Select(decimal.Floor).ToArray();
        var left = (int)(Math.Abs(remaining) * 100m - shares.Sum());
        foreach (var index in Enumerable.Range(0, fallback.Length)
            .OrderByDescending(index => raw[index] - shares[index]).ThenBy(index => index).Take(left))
            shares[index]++;
        for (var index = 0; index < fallback.Length; index++)
            taxes[fallback[index]] += Math.Sign(remaining) * shares[index] / 100m;
        return taxes;
    }
}
