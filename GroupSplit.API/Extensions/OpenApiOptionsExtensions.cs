using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GroupSplit.API.Extensions;

public class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider)
    : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var authenticationSchemes = await authenticationSchemeProvider.GetAllSchemesAsync();
        if (authenticationSchemes.All(s => s.Name != IdentityConstants.BearerScheme))
            return;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[IdentityConstants.BearerScheme] =
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Name = IdentityConstants.BearerScheme,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                BearerFormat = "Json Web Token"
            };

        var apiDescriptions = context.DescriptionGroups
            .SelectMany(g => g.Items)
            .ToList();

        // Apply it as a requirement whenever the endpoint has an Authorize attribute
        foreach (var (openApiPath, value) in document.Paths)
        {
            if (value.Operations is null)
                continue;

            foreach (var (method, operation) in value.Operations)
            {
                var httpMethod = method.ToString().ToUpperInvariant();

                var match = apiDescriptions.FirstOrDefault(a =>
                    a.HttpMethod?.Equals(httpMethod, StringComparison.OrdinalIgnoreCase) == true &&
                    a.RelativePath?.Equals(openApiPath.Trim('/'), StringComparison.OrdinalIgnoreCase) == true);

                if (match is null)
                    continue;

                var hasAuthorize = match.ActionDescriptor.EndpointMetadata
                    .OfType<AuthorizeAttribute>()
                    .Any();

                var hasAllowAnonymous = match.ActionDescriptor.EndpointMetadata
                    .OfType<AllowAnonymousAttribute>()
                    .Any();

                if (!hasAuthorize || hasAllowAnonymous)
                    continue;

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(IdentityConstants.BearerScheme, document)] = []
                });
            }
        }
    }
}