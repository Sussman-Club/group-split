namespace GroupSplit.API.Endpoints;

internal static class DeprecatedEndpointExtensions
{
    extension(RouteHandlerBuilder builder)
    {
        /// <summary>
        /// Marks a route deprecated in the OpenAPI document: still served, so a client built
        /// against it keeps working, and flagged so nothing new is built on it.
        /// </summary>
        public RouteHandlerBuilder Deprecated() =>
            builder.AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.Deprecated = true;
                return Task.CompletedTask;
            });
    }
}
