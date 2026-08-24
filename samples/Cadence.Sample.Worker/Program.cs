using Cadence;
using Cadence.Diagnostics;
using Cadence.Sample.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

// Cadence sets these itself in a future host-builder extension; until then the sample shows what
// the README asks for. ValidateScopes is what catches a scoped dependency captured by a singleton.
builder.Services.Configure<ServiceProviderOptions>(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

builder.Services.AddSingleton<IGreetingService, GreetingService>();

// The whole setup. HelloThereJob carries [ScheduledJob], so scanning this assembly finds it.
builder.Services.AddCadence(cadence => cadence.Configure(options =>
{
    options.InstanceId = $"sample:{Environment.ProcessId}";
    options.MaxConcurrentRuns = 4;
}));

// Path 1 and 2: MEL to the console, and the same MEL records exported as OpenTelemetry logs.
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});

var resource = ResourceBuilder.CreateDefault().AddService("cadence-sample-worker");

builder.Logging.AddOpenTelemetry(otel =>
{
    otel.SetResourceBuilder(resource);
    otel.IncludeScopes = true;      // without this, JobName/RunId/InstanceId are dropped
    otel.IncludeFormattedMessage = true;
    otel.AddConsoleExporter();
});

// Path 3: traces and metrics. Cadence emits under one name for both, hence the shared constant.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("cadence-sample-worker"))
    .WithTracing(tracing => tracing
        .AddSource(CadenceDiagnostics.SourceName)
        .AddConsoleExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(CadenceDiagnostics.SourceName)
        .AddConsoleExporter());

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Sample")
    .LogInformation(
        "Starting. 'hello-there' runs every 10 seconds; watch for the progress event on each span.");

await host.RunAsync();
