using GroupSplit.App.Web.Services;

namespace GroupSplit.App.Web;

public static class AndroidEndpoints
{
    public static void MapAndroidDownloads(this WebApplication app)
    {
        app.MapGet("/downloads/android/latest", async (AndroidReleaseCatalog releases, CancellationToken ct) =>
            Results.Json(await releases.LatestAsync(ct))).AllowAnonymous();
        // Public upload certificate fingerprint; the private key never leaves protected signing storage.
        app.MapGet("/.well-known/assetlinks.json", () => Results.Text("""
            [{"relation":["delegate_permission/common.handle_all_urls"],"target":{"namespace":"android_app","package_name":"club.sussman.groupsplit","sha256_cert_fingerprints":["82:5F:A2:0E:BD:F5:3C:3C:99:A8:76:21:04:B6:98:49:F6:B6:A9:55:3F:3F:F9:16:C0:0B:1C:48:FA:DB:1B:F5"]}}]
            """, "application/json")).AllowAnonymous();
    }
}
