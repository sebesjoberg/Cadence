using Cadence.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cadence.Sample.Api;

/// <summary>
/// Teaches the OpenAPI document how to authenticate to Cadence, which is what puts an Authorize
/// button in Swagger UI. Cadence.Api cannot do this itself: the transformer interfaces live in the
/// Microsoft.AspNetCore.OpenApi package rather than the shared framework, and the package refuses to
/// take a dependency that every consumer would carry whether or not it produces a document.
/// </summary>
internal static class OpenApiSecurity
{
    private const string Scheme = CadenceApiDefaults.AuthenticationScheme;

    public static OpenApiOptions AddCadenceTokenSecurity(this OpenApiOptions options)
    {
        options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            if (!await IsRegisteredAsync(context.ApplicationServices))
            {
                return;
            }

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
            document.Components.SecuritySchemes[Scheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                Description = "A token from Cadence:Api:Tokens or CADENCE_API_TOKEN. Paste the token alone; "
                    + "Swagger UI adds the Bearer prefix.",
            };
        });

        // Per operation rather than once at the top level, so the padlock and the 401 appear on the
        // endpoints that actually carry a policy. The condition is the authorization metadata
        // MapCadenceApi already stamped, so the gate's branches are read here, not re-decided.
        options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;

            if (!metadata.OfType<IAuthorizeData>().Any()
                || metadata.OfType<IAllowAnonymous>().Any()
                || !await IsRegisteredAsync(context.ApplicationServices))
            {
                return;
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(Scheme, context.Document)] = [],
            });
        });

        return options;
    }

    // No token means AddApi registers no scheme, and describing one would invite a caller to send a
    // header nothing reads.
    private static async Task<bool> IsRegisteredAsync(IServiceProvider services)
    {
        var schemes = await services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();

        return schemes.Any(scheme => string.Equals(scheme.Name, Scheme, StringComparison.Ordinal));
    }
}
