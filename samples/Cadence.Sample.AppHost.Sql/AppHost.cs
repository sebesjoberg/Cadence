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

// Aspire ships no identity-provider integration, so this is a plain container. start-dev uses the
// image's embedded database, which keeps the sample one container rather than two, and
// --import-realm reads samples/keycloak/cadence-realm.json on every start.
var keycloak = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.7.2")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http")
    .WithBindMount("../keycloak", "/opt/keycloak/data/import", isReadOnly: true)
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
    .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
    .WithArgs("start-dev", "--import-realm")
    .WithHttpHealthCheck("/realms/cadence/.well-known/openid-configuration", endpointName: "http");

// One project, three replicas, each running the tick loop and serving the control surface. §13.6
// forces that: a trigger dispatches in the process that received it, so an API tier over a separate
// worker tier could trigger none of the jobs the workers run.
//
// The port is fixed, unlike everything else Aspire assigns: Keycloak matches redirect URIs
// literally, so the address the browser comes back from has to be one the realm can name. Aspire's
// proxy still fronts the three replicas on it, which is what makes a manual run's InstanceId the
// ingress's choice rather than Cadence's.
builder.AddProject<Projects.Cadence_Sample_ClusteredWorker>("worker")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(keycloak)
    .WithHttpEndpoint(port: 5080, name: "http")
    .WithEnvironment(
        "CADENCE_OIDC_AUTHORITY",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/cadence"))
    .WithEnvironment("CADENCE_OIDC_CLIENT_ID", "cadence-dashboard")
    .WithEnvironment("CADENCE_OIDC_CLIENT_SECRET", "cadence-dashboard-secret")
    .WithReplicas(3);

builder.Build().Run();
