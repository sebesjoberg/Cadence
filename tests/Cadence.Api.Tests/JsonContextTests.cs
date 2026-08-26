using System.Text.Json;
using Cadence.Api.Internal;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Cadence.Api.Tests;

/// <summary>
/// Pins the wire shape of the one type every refused request goes out as. <c>ProblemDetails</c>
/// carries its own converter that the source generator defers to, so the context's naming policy
/// is not what decides the casing — which is exactly why it is worth asserting.
/// </summary>
public sealed class JsonContextTests
{
    [Fact]
    public void AProblemSerializesAsCamelCaseWithNoExtensionsKey()
    {
        var problem = new ProblemDetails
        {
            Status = 404,
            Type = "https://cadence.dev/problems/job-not-found",
            Title = "Job not found",
            Detail = "No job is registered under the name 'nightly'.",
        };

        var json = JsonSerializer.Serialize(problem, CadenceApiJsonContext.Default.ProblemDetails);

        Assert.DoesNotContain("extensions", json, StringComparison.Ordinal);
        Assert.Contains("\"title\":", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":404", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Title\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AProblemOmitsTheFieldsItDoesNotSet()
    {
        var problem = new ProblemDetails { Status = 400, Title = "Bad request" };

        var json = JsonSerializer.Serialize(problem, CadenceApiJsonContext.Default.ProblemDetails);

        Assert.DoesNotContain("detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("instance", json, StringComparison.Ordinal);
    }
}
