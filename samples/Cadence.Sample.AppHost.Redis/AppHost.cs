var builder = DistributedApplication.CreateBuilder(args);

// The twin of Cadence.Sample.AppHost.Sql, differing in these three lines and nothing else. One
// Redis, shared by every replica: in this tier the claim is a key only one caller can create, and
// it can only decide the race if all three replicas ask the same server.
var redis = builder.AddRedis("cadence-redis");

// One project, three replicas, each running the tick loop and serving the control surface. §13.6
// forces that: a trigger dispatches in the process that received it, so an API tier over a separate
// worker tier could trigger none of the jobs the workers run.
//
// No fixed port — three replicas cannot share one, and Aspire's proxy in front of them is what
// makes a manual run's InstanceId the ingress's choice rather than Cadence's.
builder.AddProject<Projects.Cadence_Sample_ClusteredWorker>("worker")
    .WithReference(redis)
    .WaitFor(redis)
    .WithHttpEndpoint()
    .WithReplicas(3);

builder.Build().Run();
