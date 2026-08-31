using System.Text.Json;

namespace Cadence.Execution;

/// <summary>
/// The default serializer: anything a job returns becomes JSON, using the same web defaults
/// <see cref="JobContext.Bind{TRequest}"/> reads a request with.
/// </summary>
/// <typeparam name="TResult">The type a job returns.</typeparam>
public sealed class JsonJobResultSerializer<TResult> : IJobResultSerializer<TResult>
{
    /// <inheritdoc />
    public JobResult? Serialize(TResult value)
        => value is null
            ? null
            : JobResult.Bytes(
                JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web),
                "application/json; charset=utf-8");
}
