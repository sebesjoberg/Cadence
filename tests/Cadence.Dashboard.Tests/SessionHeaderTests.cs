using System.Net;
using Cadence.Api;
using Xunit;

namespace Cadence.Dashboard.Tests;

/// <summary>
/// §4.5's CSRF rule, on the tree the dashboard's own fetches reach: a ticket without the header is
/// refused whatever admitted the request. A cross-site form cannot set a header, and a cross-origin
/// fetch that does triggers a preflight nothing answers.
/// </summary>
public sealed class SessionHeaderTests
{
    [Theory]
    [InlineData(CadenceApiDefaults.UiPath + "/jobs")]
    [InlineData(CadenceApiDefaults.UiPath + "/runs")]
    [InlineData(CadenceApiDefaults.UiPath + "/pause")]
    [InlineData(CadenceApiDefaults.UiPath + "/health/storage")]
    public async Task ACookieWithoutTheSessionHeaderIsRefused(string path)
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheSameRequestCarryingTheHeaderIsAnswered()
    {
        await using var host = await DashboardTestHost.StartWithOidcAsync();
        await host.SignInAsync("u1", "Ada");
        host.Client.DefaultRequestHeaders.Add(CadenceApiDefaults.SessionHeader, "1");

        var response = await host.Client.GetAsync(CadenceApiDefaults.UiPath + "/jobs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
