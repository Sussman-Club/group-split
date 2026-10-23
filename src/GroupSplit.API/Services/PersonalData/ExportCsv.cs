using System.Globalization;

namespace GroupSplit.API.Services.PersonalData;

internal static class ExportCsv
{
    internal static string Row(params object?[] values) => string.Join(",", values.Select(Cell));

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!
        };
        if (value is string && CouldBeFormula(text)) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static bool CouldBeFormula(string text)
    {
        if (text.Length == 0) return false;
        if ("\t\r\n".Contains(text[0])) return true;
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && "=+-@＝＋－＠".Contains(trimmed[0]);
    }
}
