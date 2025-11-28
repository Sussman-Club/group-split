using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace GroupSplit.API.Extensions;

public static class OpenApiOptionsExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddOpenApiDocuments()
        {
            if (Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
            {
                services.AddOpenApi("api",options =>
                {
                    options.ShouldInclude = description =>
                    {
                        var path = description.RelativePath ?? string.Empty;
                        return !path.StartsWith("identity", StringComparison.OrdinalIgnoreCase);
                    };
                    
                    options.AddBearerTokenAuthentication();
                });

                services.AddOpenApi("identity",options =>
                {
                    options.ShouldInclude = description =>
                    {
                        var path = description.RelativePath ?? string.Empty;
                        return path.StartsWith("identity", StringComparison.OrdinalIgnoreCase);
                    };

                    options.AddDocumentTransformer(async (doc, ctx, ct) =>
                    {
                        var schema1 = await ctx.GetOrCreateSchemaAsync(
                            typeof(ProblemDetails),
                            cancellationToken: ct);
                        
                        doc.AddComponent("ProblemDetails", schema1);
                    });
                    
                    options.AddBearerTokenAuthentication();
                });
            }
            else
            {
                services.AddOpenApi(options => options.AddBearerTokenAuthentication());
            }

            return services;
        }
    }
    
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