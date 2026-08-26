using Cadence;
using Cadence.Api;
using Cadence.Sample.Api;
using Cadence.Storage.Sql;

var builder = WebApplication.CreateBuilder(args);

// Injected by the AppHost's WithReference(database). A standalone run has no infrastructure and no
// connection string, and takes the in-memory stores instead — those are the two shapes this sample
// has, and the only difference between them.
var connectionString = builder.Configuration.GetConnectionString("cadence");

builder.Services.AddCadence(cadence =>
{
    cadence
        .Configure(options => options.InstanceId = $"sample-api:{Environment.ProcessId}")
        .AddApi();

    if (connectionString is not null)
    {
        cadence.UseSqlStorage(connectionString);
    }
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi(openApi => openApi.AddCadenceTokenSecurity());
}

var app = builder.Build();

if (connectionString is null)
{
    app.Logger.UsingInMemoryStorage();
}
else
{
    app.Logger.UsingSqlStorage();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(ui => ui.SwaggerEndpoint("/openapi/v1.json", "Cadence control surface"));
}

// The gate: this throws outside Development unless something will authenticate the tree. What
// satisfies it here is the token in appsettings.Development.json.
app.MapCadenceApi();
app.MapCadenceHealth();

await app.RunAsync();
