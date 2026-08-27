var builder = DistributedApplication.CreateBuilder(args);

// One database, shared by every replica. The unique index on CadenceJobRun is what decides which
// replica gets to start a given occurrence, and it can only decide that if they all ask the same
// database.
//
// The resource is named cadence-sql and the database inside it is still called cadence: the worker
// picks its storage tier from which connection-string name it was handed, and Cadence.Sample.AppHost.Redis
// hands it cadence-redis instead. Nothing else differs between the two AppHosts.
var database = builder.AddSqlServer("sql")
    .AddDatabase("cadence-sql", databaseName: "cadence");

// One project, three replicas, each running the tick loop and serving the control surface. §13.6
// forces that: a trigger dispatches in the process that received it, so an API tier over a separate
// worker tier could trigger none of the jobs the workers run.
//
// No fixed port — three replicas cannot share one, and Aspire's proxy in front of them is what
// makes a manual run's InstanceId the ingress's choice rather than Cadence's.
builder.AddProject<Projects.Cadence_Sample_ClusteredWorker>("worker")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpEndpoint()
    .WithReplicas(3);

builder.Build().Run();
