using GroupSplit.API.Services;

namespace GroupSplit.API.Endpoints;

public static class MerchantImagesApi
{
    public static void MapMerchantImagesApi(this IEndpointRouteBuilder routes)
    {
        // These are shared brand images, not receipt files. The opaque ID names one PNG only.
        routes.MapGet("/merchant-images/{imageId:guid}.png", async (
            Guid imageId, MerchantImageService images, HttpResponse response, CancellationToken ct) =>
        {
            var bytes = await images.ReadAsync(imageId, ct);
            response.Headers.CacheControl = "public,max-age=31536000,immutable";
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(bytes, "image/png");
        }).AllowAnonymous().ExcludeFromDescription();
    }
}
