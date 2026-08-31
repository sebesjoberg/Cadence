using Cadence.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Cadence.Dashboard.Internal;

/// <summary>Serves the hashed bundle assets out of the assembly.</summary>
/// <remarks>
/// An endpoint rather than static-file middleware, because middleware would make the host
/// responsible for calling <c>UseStaticFiles</c> and for calling it in the right place: a package
/// that maps its own routes should not also impose an ordering constraint on a pipeline it does not
/// own. Mapping it also means routing answers the miss, so an unknown asset is a 404 rather than the
/// shell with a 200.
/// </remarks>
internal static class AssetEndpoints
{
    /// <summary>Safe because Vite hashes every filename: a changed file is a changed URL.</summary>
    private const string CacheControl = "public, max-age=31536000, immutable";

    private const string AssetDirectory = "assets";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Maps the asset route under <see cref="CadenceApiDefaults.AssetsPath"/>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="files">The embedded bundle.</param>
    public static void Map(IEndpointRouteBuilder endpoints, IFileProvider files)
    {
        // Anonymous for the shell's reason: the application has to load before it can sign in.
        endpoints.MapGet($"{CadenceApiDefaults.AssetsPath}/{{**path}}", (HttpContext context, string path) =>
        {
            var file = files.GetFileInfo($"{AssetDirectory}/{path}");

            if (!file.Exists || file.IsDirectory)
            {
                return Results.NotFound();
            }

            if (!ContentTypes.TryGetContentType(file.Name, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            context.Response.Headers.CacheControl = CacheControl;

            return Results.Stream(file.CreateReadStream(), contentType);
        }).AllowAnonymous();
    }
}
