namespace GroupSplit.Shared;

public readonly record struct ReceiptTaxLine(decimal TotalPrice, decimal TaxAmount, decimal? TaxRate, bool? IsTaxable);

public static class ReceiptTaxes
{
    /// <summary>Allocates otherwise unassigned tax by price, retaining explicit line tax and exemptions.</summary>
    public static decimal[]? DistributeUnassigned(decimal totalTax, IReadOnlyList<ReceiptTaxLine> items, int decimalPlaces = 2)
    {
        if (totalTax <= 0 || decimal.Round(totalTax, decimalPlaces) != totalTax ||
            !items.Any(item => item.IsTaxable != false && item.TaxRate is null &&
                item.TaxAmount == 0m && item.TotalPrice > 0m))
        {
            return null;
        }

        if (Calculate(totalTax, items, decimalPlaces: decimalPlaces).Sum() >= totalTax)
        {
            return null;
        }

        var result = Calculate(totalTax, items, allocateUnidentified: true, decimalPlaces: decimalPlaces);
        if (result.Sum() != totalTax || items.Select((item, index) => (item, index))
                .Any(pair => pair.item.TaxAmount != 0m && result[pair.index] != pair.item.TaxAmount))
        {
            return null;
        }

        return result;
    }

    public static decimal[] Calculate(decimal totalTax, IReadOnlyList<ReceiptTaxLine> items, bool allocateUnidentified = false, int decimalPlaces = 2)
    {
        var scale = decimalPlaces switch { 0 => 1m, 1 => 10m, 2 => 100m, 3 => 1000m, _ => throw new ArgumentOutOfRangeException(nameof(decimalPlaces)) };
        // A reconciled saved bill already includes its cent allocation. Preserve it on reread.
        if (items.Any(item => item.TaxAmount != 0m)
            && items.All(item => item.IsTaxable != false || item.TaxAmount == 0m)
            && items.Sum(item => item.TaxAmount) == totalTax)
        {
            return items.Select(item => item.TaxAmount).ToArray();
        }

        var exact = items.Select(item => item.IsTaxable == false ? 0m
            : item.TaxAmount != 0m ? item.TaxAmount
            : item.TaxRate is { } rate ? item.TotalPrice * rate / 100m : item.TaxAmount).ToArray();
        var taxes = exact.Select(value => decimal.Round(value, decimalPlaces, MidpointRounding.AwayFromZero)).ToArray();
        var remaining = totalTax - taxes.Sum();
        if (remaining == 0)
        {
            return taxes;
        }

        var rated = Enumerable.Range(0, items.Count)
            .Where(index => items[index].IsTaxable != false && items[index].TaxRate is > 0
                && items[index].TaxAmount == 0m && items[index].TotalPrice != 0).ToArray();
        var manual = Enumerable.Range(0, items.Count)
            .Where(index => items[index].IsTaxable != false && items[index].TaxRate is null
                && items[index].TotalPrice != 0).ToArray();
        var missing = manual.Where(index => (allocateUnidentified || items[index].IsTaxable == true) && items[index].TaxAmount == 0m && (remaining > 0
            ? items[index].TotalPrice > 0 : items[index].TotalPrice < 0)).ToArray();
        var fillMissing = missing.Length > 0 && (allocateUnidentified
            || items.Any(item => item.TaxRate is not null || item.IsTaxable is not null));
        // Correct cent rounding without hiding a materially incorrect receipt reading.
        var candidates = rated;
        if (!fillMissing && Math.Abs(remaining) <= 0.5m / scale * candidates.Length)
        {
            var direction = Math.Sign(remaining);
            var cents = (int)(Math.Abs(remaining) * scale);
            foreach (var index in candidates
                .OrderByDescending(index => direction * (exact[index] - taxes[index]))
                .ThenBy(index => index).Take(cents))
            {
                taxes[index] += direction / scale;
            }

            return taxes;
        }

        if (!allocateUnidentified && items.All(item => item.TaxRate is null && item.IsTaxable is null))
        {
            return taxes;
        }

        // Identified taxable lines can carry tax not otherwise assigned by the reading.
        // A printed/manual line tax is evidence, not an unidentified amount. Only an
        // explicit redistribution may change it beyond a rounding cent.
        var fallback = fillMissing ? missing : allocateUnidentified ? manual.Where(index => remaining > 0
            ? items[index].TotalPrice > 0 : items[index].TotalPrice < 0).ToArray() : [];
        var weight = fallback.Sum(index => Math.Abs(items[index].TotalPrice));
        if (weight == 0)
        {
            return taxes;
        }

        var raw = fallback.Select(index => Math.Abs(remaining) * scale * Math.Abs(items[index].TotalPrice) / weight).ToArray();
        var shares = raw.Select(decimal.Floor).ToArray();
        var left = (int)(Math.Abs(remaining) * scale - shares.Sum());
        foreach (var index in Enumerable.Range(0, fallback.Length)
            .OrderByDescending(index => raw[index] - shares[index]).ThenBy(index => index).Take(left))
        {
            shares[index]++;
        }

        for (var index = 0; index < fallback.Length; index++)
        {
            taxes[fallback[index]] += Math.Sign(remaining) * shares[index] / scale;
        }

        return taxes;
    }
}
