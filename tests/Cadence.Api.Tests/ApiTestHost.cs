using Cadence.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cadence.Api.Tests;

/// <summary>Builds a running control surface over the in-memory defaults, so tests exercise real routing and auth.</summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private ApiTestHost(IHost host) => _host = host;

    public HttpClient Client { get; private init; } = null!;

    public static async Task<ApiTestHost> StartAsync(
        Action<CadenceApiOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        LogCapture? logs = null,
        IDictionary<string, string?>? configuration = null)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment(environment ?? Environments.Production);

            web.ConfigureAppConfiguration(config =>
            {
                if (configuration is not null)
                {
                    config.AddInMemoryCollection(configuration);
                }
            });

            web.ConfigureServices(collection =>
            {
                if (logs is not null)
                {
                    collection.AddSingleton<ILoggerProvider>(logs);
                }

                collection.AddRouting();
                collection.AddCadence(cadence => cadence.AddApi(configure ?? (_ => { })));
                services?.Invoke(collection);
            });

            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapCadenceApi());
            });
        });

        var host = await builder.StartAsync();

        return new ApiTestHost(host) { Client = host.GetTestClient() };
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
