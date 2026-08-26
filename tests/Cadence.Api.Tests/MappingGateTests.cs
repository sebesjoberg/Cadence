using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// §13.3: the API refuses to mount when nothing would authenticate it, and refuses at map time so
/// the failure lands on a deploy rather than on whoever finds the open endpoint first.
/// </summary>
public sealed class MappingGateTests
{
    [Fact]
    public void MappingOutsideDevelopmentWithNothingConfiguredThrows()
    {
        var app = BuildApp(Environments.Production);

        Assert.Throws<CadenceStartupException>(() => app.MapCadenceApi());
    }

    [Fact]
    public void MappingInDevelopmentWithNothingConfiguredIsAllowed()
    {
        var app = BuildApp(Environments.Development);

        app.MapCadenceApi();
    }

    [Fact]
    public void AConfiguredTokenSatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.Tokens.Add("s3cret-token-value-32-chars-long"));

        app.MapCadenceApi();
    }

    [Fact]
    public void AllowUnauthenticatedSatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.AllowUnauthenticated = true);

        app.MapCadenceApi();
    }

    [Fact]
    public void ANamedPolicySatisfiesTheGateInProduction()
    {
        var app = BuildApp(Environments.Production, api => api.RequireAuthorization("cadence-ops"));

        app.MapCadenceApi();
    }

    [Fact]
    public void MappingInDevelopmentWithNothingConfiguredWarns()
    {
        var logs = new LogCapture();
        var app = BuildApp(Environments.Development, logs: logs);

        app.MapCadenceApi();

        Assert.True(logs.HasWarning(3000));
    }

    [Fact]
    public void AllowUnauthenticatedWarnsOnEveryStart()
    {
        var logs = new LogCapture();
        var app = BuildApp(Environments.Production, api => api.AllowUnauthenticated = true, logs);

        app.MapCadenceApi();

        Assert.True(logs.HasWarning(3001));
    }

    private static WebApplication BuildApp(
        string environment,
        Action<CadenceApiOptions>? configure = null,
        LogCapture? logs = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });

        if (logs is not null)
        {
            builder.Services.AddSingleton<ILoggerProvider>(logs);
        }

        builder.Services.AddCadence(cadence => cadence.AddApi(configure ?? (_ => { })));

        return builder.Build();
    }
}
