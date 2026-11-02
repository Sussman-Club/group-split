namespace GroupSplit.App.Shared.Models;

/// <summary>A logo draft. Uploaded bytes stay local until the merchant form is saved.</summary>
public sealed record MerchantLogoSelection(string? Url, byte[]? UploadedPng = null, string? Preview = null)
{
    public static MerchantLogoSelection FromUpload(byte[] png) =>
        new(null, png, "data:image/png;base64," + Convert.ToBase64String(png));
}
