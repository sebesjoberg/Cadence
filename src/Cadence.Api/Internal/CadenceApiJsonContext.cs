using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Cadence.Api.Internal;

/// <summary>
/// Serialization for every response the control surface returns. Source-generated so the package
/// stays trim-friendly and so adding a response shape is a deliberate act.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TriggerResponse))]
[JsonSerializable(typeof(JobSummaryResponse))]
[JsonSerializable(typeof(IReadOnlyList<JobSummaryResponse>))]
[JsonSerializable(typeof(JobDetailResponse))]
[JsonSerializable(typeof(RunSummaryResponse))]
[JsonSerializable(typeof(RunDetailResponse))]
[JsonSerializable(typeof(RunPageResponse))]
[JsonSerializable(typeof(PauseResponse))]
[JsonSerializable(typeof(PauseRequest))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class CadenceApiJsonContext : JsonSerializerContext;
