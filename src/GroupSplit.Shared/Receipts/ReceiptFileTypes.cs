namespace GroupSplit.Shared;

public static class ReceiptFileTypes
{
    public const string Accept = ".jpg,.jpeg,.jpe,.jpge,.png,.webp,.pdf,image/jpeg,image/png,image/webp,application/pdf";

    public static string? Resolve(string fileName, string? contentType)
    {
        var expected = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jpge" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => null
        };
        if (expected is null)
            return null;
        var supplied = contentType?.Split(';', 2)[0].Trim();
        // Some document pickers provide no MIME type, or only a generic binary type.
        // Infer from the supported extension in that case; explicit conflicts stay invalid.
        return string.IsNullOrWhiteSpace(supplied)
               || supplied.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
               || supplied.Equals(expected, StringComparison.OrdinalIgnoreCase)
               || expected == "image/jpeg" && supplied.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
            ? expected : null;
    }
}
