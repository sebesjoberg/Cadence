using Cadence;
using Cadence.Diagnostics;
using Cadence.Sample.ClusteredWorker;
using Cadence.Storage.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ServiceProviderOptions>(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Injected by the AppHost's WithReference(db). Failing loudly here beats failing later inside the
// migrator with a connection string nobody set.
var connectionString = builder.Configuration.GetConnectionString("cadence")
    ?? throw new InvalidOperationException(
        "No 'cadence' connection string. This project is meant to be launched by " +
        "Cadence.Sample.AppHost, which supplies one; run the AppHost rather than this project.");

var instanceId = ReplicaIdentity.Resolve();

builder.Services.AddCadence(cadence => cadence
    .Configure(options =>
    {
        options.InstanceId = instanceId;
        options.MaxConcurrentRuns = 4;
    })
    .UseSqlStorage(connectionString, sql =>
    {
        // Demo timings, not production ones. The defaults are 15s / 60s / 5min, which are right for
        // a real deployment and wrong for standing in front of a screen: killing a replica would
        // take five minutes to show anything. Shortened here so the janitor reaps a dead replica's
        // runs within about half a minute.
        //
        // The relationship the defaults encode still holds — the timeout is four heartbeats, so one
        // missed beat never gets a live replica's runs marked Lost.
        sql.HeartbeatInterval = TimeSpan.FromSeconds(5);
        sql.HeartbeatTimeout = TimeSpan.FromSeconds(20);
        sql.JanitorInterval = TimeSpan.FromSeconds(15);

        // Bounds how long a schedule edit takes to reach the other replicas. Short so the demo in
        // the README does not need a coffee break.
        sql.SchedulePollInterval = TimeSpan.FromSeconds(5);
    }));

builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});

// Aspire supplies OTEL_EXPORTER_OTLP_ENDPOINT and the dashboard's api-key header through the
// environment, and AddOtlpExporter reads both from there — so there is no endpoint to configure
// here, and running this project outside the AppHost simply exports nowhere.
var resource = ResourceBuilder.CreateDefault()
    .AddService("cadence-clustered-worker", serviceInstanceId: instanceId);

builder.Logging.AddOpenTelemetry(otel =>
{
    otel.SetResourceBuilder(resource);
    otel.IncludeScopes = true;      // without this, JobName/RunId/InstanceId are dropped
    otel.IncludeFormattedMessage = true;
    otel.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("cadence-clustered-worker", serviceInstanceId: instanceId))
    .WithTracing(tracing => tracing
        .AddSource(CadenceDiagnostics.SourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(CadenceDiagnostics.SourceName)
        .AddOtlpExporter());

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Sample")
    .ReplicaStarting(instanceId);

await host.RunAsync();

namespace Cadence.Sample.ClusteredWorker
{
    /// <summary>
    /// Works out a name for this replica that means something in the dashboard.
    /// </summary>
    /// <remarks>
    /// Cadence's default instance id is <c>{machine}:{pid}:{short-guid}</c>, which is correctly
    /// unique but tells you nothing when three replicas of the same project are side by side in a
    /// resource list. Aspire has already named them: it passes the resource name as
    /// <c>OTEL_SERVICE_NAME</c> and the per-replica suffix as <c>service.instance.id</c> inside
    /// <c>OTEL_RESOURCE_ATTRIBUTES</c>, and shows the two joined — <c>worker-eqdkkxhb</c> — as the
    /// row in the dashboard. Rebuilding that same string here is what lets you read an
    /// <c>InstanceId</c> out of run history and go straight to the replica it came from.
    /// </remarks>
    internal static class ReplicaIdentity
    {
        public static string Resolve()
        {
            var replica = ResourceAttribute("service.instance.id");

            if (replica is null)
            {
                // Outside Aspire there is nothing better to say, so fall back to what Cadence would
                // have chosen on its own.
                return new CadenceOptions().InstanceId;
            }

            var service = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");

            return string.IsNullOrWhiteSpace(service) ? replica : $"{service}-{replica}";
        }

        private static string? ResourceAttribute(string key)
        {
            var attributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");

            if (string.IsNullOrWhiteSpace(attributes))
            {
                return null;
            }

            foreach (var pair in attributes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);

                if (separator > 0 &&
                    pair.AsSpan(0, separator).Trim().SequenceEqual(key) &&
                    pair.AsSpan(separator + 1).Trim().Length > 0)
                {
                    return pair[(separator + 1)..].Trim();
                }
            }

            return null;
        }
    }
}
