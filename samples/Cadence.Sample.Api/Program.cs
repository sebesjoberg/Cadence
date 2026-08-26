using Cadence;
using Cadence.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCadence(cadence => cadence
    .Configure(options => options.InstanceId = $"sample-api:{Environment.ProcessId}")
    .AddApi());

var app = builder.Build();

// The gate: this throws outside Development unless something will authenticate the tree. What
// satisfies it here is the token in appsettings.Development.json.
app.MapCadenceApi();
app.MapCadenceHealth();

await app.RunAsync();
