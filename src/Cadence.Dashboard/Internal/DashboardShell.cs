using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cadence.Api;
using Cadence.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Cadence.Dashboard.Internal;

/// <summary>
/// The single HTML document every dashboard route answers with, and the bootstrap object baked
/// into it.
/// </summary>
/// <remarks>
/// Built once, at map time, and served as a cached string. That is a constraint rather than an
/// optimisation: a document composed per request would invite per-request facts into it, and
/// anything true of one caller and not another belongs in a response the SPA fetches, where the
/// operator tree's policies still govern it. What the bootstrap carries is what is true of the
/// deployment — its name, and which capabilities the container was given.
/// </remarks>
internal static class DashboardShell
{
    private const string ShellFile = "index.html";

    private const string BootPlaceholder = "__CADENCE_BOOT__";

    /// <summary>Renders the shell against the container the dashboard was mapped on.</summary>
    /// <param name="services">The application's container.</param>
    /// <param name="files">The embedded bundle.</param>
    /// <exception cref="InvalidOperationException">The bundle is missing or does not carry the placeholder.</exception>
    public static string Render(IServiceProvider services, IFileProvider files)
    {
        var file = files.GetFileInfo(ShellFile);

        if (!file.Exists)
        {
            throw new InvalidOperationException(
                $"Cadence.Dashboard's assembly carries no {ShellFile}. The bundle is built by the " +
                "package's own MSBuild target and embedded from wwwroot; a build that skipped it " +
                "produces an assembly that cannot serve anything.");
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        if (!html.Contains(BootPlaceholder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The embedded {ShellFile} does not carry the {BootPlaceholder} placeholder, so the " +
                "SPA would load with no bootstrap and fail in the browser instead of here.");
        }

        var options = services.GetRequiredService<IOptions<CadenceApiOptions>>().Value;

        var boot = new DashboardBoot(
            options.Dashboard.Title,
            new DashboardCapabilities(
                ScheduleWrite: services.GetService<IWritableScheduleSource>() is not null,
                Tokens: services.GetService<IWritableApiTokenStore>() is not null));

        // The default encoder escapes <, > and &, which is what keeps a configured title from
        // closing the script element it is written into.
        var json = JsonSerializer.Serialize(boot, DashboardJsonContext.Default.DashboardBoot);

        return html.Replace(BootPlaceholder, json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Maps the routes that answer with the shell, and the two that must not.
    /// </summary>
    /// <remarks>
    /// The SPA routes on the client, so every path under the base one is the same document. The
    /// exclusions are for the deployment that mapped the dashboard alone: without them a request to
    /// an endpoint <c>MapCadenceApi()</c> would have mapped falls through to the catch-all and
    /// answers 200 with HTML, which no client can tell from a working API.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="html">The rendered shell.</param>
    public static void Map(IEndpointRouteBuilder endpoints, string html)
    {
        IResult Serve(HttpContext context)
        {
            // The document names hashed assets, so a cached copy outlives the files it points at.
            context.Response.Headers.CacheControl = "no-cache";

            return TypedResults.Text(html, "text/html", Encoding.UTF8);
        }

        // Anonymous on purpose: a browser has to load the application before it can sign in, and
        // every fetch the application then makes is governed by the operator tree's own policies.
        endpoints.MapGet(CadenceApiDefaults.BasePath, Serve).AllowAnonymous();
        endpoints.MapGet($"{CadenceApiDefaults.BasePath}/{{**path}}", Serve).AllowAnonymous();

        // Literal segments outrank a catch-all, so the mapped trees win over these.
        endpoints.MapGet($"{CadenceApiDefaults.ApiPath}/{{**path}}", () => Results.NotFound());
        endpoints.MapGet($"{CadenceApiDefaults.UiPath}/{{**path}}", () => Results.NotFound());
    }
}

/// <summary>What the SPA reads out of <c>window.__cadence</c> before its first fetch.</summary>
/// <param name="Title">Names the deployment, so two open tabs are told apart.</param>
/// <param name="Capabilities">What this container can do, decided by which services are registered.</param>
internal sealed record DashboardBoot(string Title, DashboardCapabilities Capabilities);

/// <summary>
/// The capability facts, each one the presence of a service rather than a setting: the routes that
/// back them are mounted on the same condition, so a control the SPA renders is a route that exists.
/// </summary>
/// <param name="ScheduleWrite">A writable schedule source is registered.</param>
/// <param name="Tokens">A writable token store is registered.</param>
internal sealed record DashboardCapabilities(bool ScheduleWrite, bool Tokens);

/// <summary>Serialization for the bootstrap. Source-generated, so the package stays trim-friendly.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardBoot))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
