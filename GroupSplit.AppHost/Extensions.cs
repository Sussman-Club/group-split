namespace GroupSplit.AppHost;

public static class Extensions
{
    extension<T>(IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        public IResourceBuilder<T> WithScalarUrl()
        {
            return builder
                .WithUrls(ctx =>
                {
                    foreach (var url in ctx.Urls.Where(x => x.Endpoint?.EndpointName is "http" or "https"))
                    {
                        url.DisplayLocation = UrlDisplayLocation.DetailsOnly;
                    }
                })
                .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation
                {
                    Url = "/scalar/v1",
                    DisplayLocation = UrlDisplayLocation.SummaryAndDetails
                });
        }
    }
}