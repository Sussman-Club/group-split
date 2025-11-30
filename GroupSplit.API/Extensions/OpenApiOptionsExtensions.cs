using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
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
                services.AddOpenApi("api", options =>
                {
                    options.ShouldInclude = description => !IdentityPath(description);

                    options.CreateSchemaReferenceId = typeInfo =>
                    {
                        if (typeInfo.Type is not { IsGenericType: true, GenericTypeArguments: [var modelType] } type ||
                            type.GetGenericTypeDefinition() != typeof(JsonPatchDocument<>))
                        {
                            return OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
                        }

                        var modelTypeInfo = JsonTypeInfo.CreateJsonTypeInfo(modelType, typeInfo.Options);

                        return $"JsonPatchDocumentOf{OpenApiOptions.CreateDefaultSchemaReferenceId(modelTypeInfo)}";
                    };

                    options.AddSchemaTransformer((schema, context, _) =>
                    {
                        if (context.JsonTypeInfo.Type is not
                                { IsGenericType: true, GenericTypeArguments: [var modelType] } type ||
                            type.GetGenericTypeDefinition() != typeof(JsonPatchDocument<>)) return Task.CompletedTask;

                        var modelTypeInfo = JsonTypeInfo.CreateJsonTypeInfo(modelType, context.JsonTypeInfo.Options);
                        schema.Title = "JsonPatchDocumentOf" + OpenApiOptions.CreateDefaultSchemaReferenceId(modelTypeInfo);

                        return Task.CompletedTask;
                    });

                    options.AddBearerTokenAuthentication();
                });

                services.AddOpenApi("identity", options =>
                {
                    options.ShouldInclude = IdentityPath;

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

            bool IdentityPath(ApiDescription description)
            {
                var path = description.RelativePath ?? string.Empty;
                return path.StartsWith("identity", StringComparison.OrdinalIgnoreCase);
            }
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