using Cadence;
using Cadence.Api;
using Cadence.Diagnostics;
using Cadence.Sample.ClusteredWorker;
using Cadence.Storage.Redis;
using Cadence.Storage.Sql;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// A web host, not a worker host, and every replica is one. §13.6: a trigger dispatches in the
// process that received it, so a replica without the API is a replica no trigger can reach.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServiceProviderOptions>(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// This one project is both clustered samples. Which storage tier it uses is decided by which
// connection string an AppHost injected: Cadence.Sample.AppHost.Redis supplies cadence-redis,
// Cadence.Sample.AppHost.Sql supplies cadence-sql, and running this project on its own supplies
// neither, which is the in-memory path.
var redisConnectionString = builder.Configuration.GetConnectionString("cadence-redis");
var sqlConnectionString = builder.Configuration.GetConnectionString("cadence-sql");

var tier = redisConnectionString is not null ? "Redis"
    : sqlConnectionString is not null ? "SQL Server"
    : "in-memory";

var instanceId = ReplicaIdentity.Resolve();

// Demo timings, not production ones, and the same numbers on both tiers so a difference between the
// samples can only come from the store. The defaults are 15s / 60s / 5min / 10s, which are right for
// a real deployment and wrong for standing in front of a screen: killing a replica would take five
// minutes to show anything.
//
// The relationship the defaults encode still holds — the timeout is four heartbeats, so one missed
// beat never gets a live replica's runs marked Lost.
var heartbeatInterval = TimeSpan.FromSeconds(5);
var heartbeatTimeout = TimeSpan.FromSeconds(20);
var janitorInterval = TimeSpan.FromSeconds(15);
var schedulePollInterval = TimeSpan.FromSeconds(5);

builder.Services.AddCadence(cadence =>
{
    cadence
        .Configure(options =>
        {
            options.InstanceId = instanceId;
            options.MaxConcurrentRuns = 4;
        })
        .AddApi();

    if (redisConnectionString is not null)
    {
        cadence.UseRedisStorage(redisConnectionString, redis =>
        {
            redis.HeartbeatInterval = heartbeatInterval;
            redis.HeartbeatTimeout = heartbeatTimeout;
            redis.JanitorInterval = janitorInterval;

            // Kept short even though pub/sub delivers an edit in milliseconds: §11.3 — pub/sub has
            // no redelivery, so the poll is the backstop, not the mechanism.
            redis.SchedulePollInterval = schedulePollInterval;
        });
    }
    else if (sqlConnectionString is not null)
    {
        cadence.UseSqlStorage(sqlConnectionString, sql =>
        {
            sql.HeartbeatInterval = heartbeatInterval;
            sql.HeartbeatTimeout = heartbeatTimeout;
            sql.JanitorInterval = janitorInterval;
            sql.SchedulePollInterval = schedulePollInterval;
        });
    }
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi(openApi => openApi.AddCadenceTokenSecurity());
}

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(ui => ui.SwaggerEndpoint("/openapi/v1.json", "Cadence control surface"));
}

// The gate: this throws outside Development unless something will authenticate the tree. What
// satisfies it here is the token in appsettings.Development.json.
app.MapCadenceApi();
app.MapCadenceHealth();

app.Logger.ReplicaStarting(instanceId, tier);

await app.RunAsync();

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
