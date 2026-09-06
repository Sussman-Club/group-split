using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using GroupSplit.Shared;
using Microsoft.AspNetCore.JsonPatch.SystemTextJson;
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
                    options.CreateSchemaReferenceId = typeInfo =>
                    {
                        if (typeInfo.Type is not { IsGenericType: true, GenericTypeArguments: [var modelType] } type)
                        {
                            return OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
                        }

                        var definition = type.GetGenericTypeDefinition();

                        if (definition != typeof(JsonPatchDocument<>) && definition != typeof(PagedResponse<>))
                        {
                            return OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
                        }

                        var modelTypeInfo = JsonTypeInfo.CreateJsonTypeInfo(modelType, typeInfo.Options);
                        var argumentName = OpenApiOptions.CreateDefaultSchemaReferenceId(modelTypeInfo);

                        // The name a client compiles against. For the patch documents the
                        // templates turn it back into a generic; for a page they do not --
                        // the rewrite they carry is for parameters only -- so the name lands
                        // in the generated client as written and there is a type of exactly
                        // this name in GroupSplit.Shared to meet it. Pinned here rather than
                        // left to the default so the two cannot drift apart.
                        return definition == typeof(JsonPatchDocument<>)
                            ? $"JsonPatchDocumentOf{argumentName}"
                            : $"PagedResponseOf{argumentName}";
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