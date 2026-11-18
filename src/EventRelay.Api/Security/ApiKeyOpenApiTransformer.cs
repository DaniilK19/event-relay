using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EventRelay.Api.Security;

/// <summary>Describes the X-Api-Key header in the OpenAPI document, so the API reference can send it.</summary>
public sealed class ApiKeyOpenApiTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "Publisher keys can call /events. Admin keys can call everything, including /subscriptions.",
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document)] = [],
        });

        return Task.CompletedTask;
    }
}
