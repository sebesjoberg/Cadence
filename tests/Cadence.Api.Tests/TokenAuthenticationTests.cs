using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>§13.3: the token scheme, and what it refuses.</summary>
public sealed class TokenAuthenticationTests
{
    private const string Token = "s3cret-token-value-32-chars-long";

    [Fact]
    public async Task ACorrectTokenIsAuthenticated()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.SendAsync(Request(Token));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWrongTokenIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.SendAsync(Request("not-the-token-but-the-same-length"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMissingHeaderIsRefused()
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var response = await host.Client.GetAsync("/cadence/api/pause");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "dXNlcjpwYXNz")]
    [InlineData("Bearer", "")]
    public async Task AMalformedHeaderIsRefused(string scheme, string parameter)
    {
        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token));

        var request = new HttpRequestMessage(HttpMethod.Get, "/cadence/api/pause");
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, parameter);

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokensBindFromConfiguration()
    {
        await using var host = await ApiTestHost.StartAsync(
            configuration: new Dictionary<string, string?> { ["Cadence:Api:Tokens:0"] = Token });

        var response = await host.Client.SendAsync(Request(Token));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokensBindFromTheEnvironmentVariableSplitOnCommas()
    {
        await using var host = await ApiTestHost.StartAsync(
            configuration: new Dictionary<string, string?>
            {
                ["CADENCE_API_TOKEN"] = $" first-token-value-32-chars-long , {Token} ,",
            });

        var response = await host.Client.SendAsync(Request(Token));

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BootLogsTheSourceAndCountButNeverTheToken()
    {
        var logs = new LogCapture();

        await using var host = await ApiTestHost.StartAsync(api => api.Tokens.Add(Token), logs: logs);

        Assert.DoesNotContain(logs.Records, record => record.Message.Contains(Token, StringComparison.Ordinal));
        Assert.Contains(logs.Records, record => record.EventId == 3002);
    }

    private static HttpRequestMessage Request(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/cadence/api/pause");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
