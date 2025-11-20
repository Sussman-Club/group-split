using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GroupSplit.API.Extensions;

public static class OpenApiOptionsExtensions
{
    extension(OpenApiOptions options)
    {
        public OpenApiOptions AddBearerTokenAuthentication()
        {
            var scheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Name = IdentityConstants.BearerScheme,
                Scheme = "Bearer"
            };

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes.Add(IdentityConstants.BearerScheme, scheme);
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Any())
                    return Task.CompletedTask;

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(IdentityConstants.BearerScheme, context.Document)] = []
                });

                return Task.CompletedTask;
            });

            return options;
        }
    }
}