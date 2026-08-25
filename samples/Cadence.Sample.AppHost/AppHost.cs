var builder = DistributedApplication.CreateBuilder(args);

// One database, shared by every replica. That sharing is the entire point: the unique index on
// CadenceJobRun is what decides which replica gets to start a given occurrence, and it can only
// decide that if they are all asking the same database.
var database = builder.AddSqlServer("sql")
    .AddDatabase("cadence");

// Three, not two. Two would be enough to show the guarantee, but three is what makes the shape of
// it obvious: one replica wins every occurrence and two sit idle, which reads as a leader and its
// standbys rather than as a coin flip that happened to land the same way twice. It also leaves a
// second replica to fail over to when you kill the first one.
builder.AddProject<Projects.Cadence_Sample_ClusteredWorker>("worker")
    .WithReference(database)
    .WaitFor(database)
    .WithReplicas(3);

builder.Build().Run();
