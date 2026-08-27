# Samples

| Sample | What it is for |
|---|---|
| `Cadence.Sample.Worker` | The telemetry fan-out, on the zero-infrastructure path. One job, in-memory stores, OTel to the console. |
| `Cadence.Sample.ClusteredWorker` | The deployable shape: Core, the SQL tier and the control surface in **one** web host. Launched by the AppHost, not directly. |
| `Cadence.Sample.AppHost` | Aspire, a SQL Server container, and three replicas of that host behind one proxied endpoint. |

## Cadence.Sample.Worker

A worker host that runs one job, `hello-there`, every ten seconds. It greets a different name each
run and does nothing else. The point is not the job — it is what surrounds it.

**It consumes Cadence as a NuGet package, not as a project reference.** A project reference would
hide packaging mistakes until the first real publish. This one found `NU5039` (a declared
`PackageReadmeFile` that was never packed) on its first run, which is exactly the job it is for.

```powershell
./scripts/pack.ps1                                  # build the local feed
dotnet run --project samples/Cadence.Sample.Worker  # ctrl+c to stop
```

### What it proves

Progress a job reports fans out to three places, and the sample shows all three at once:

| Path | What you see |
|---|---|
| **MEL** (`ILogger`) | `info: ...HelloThereJob[1] Hello there, Tony!` on the console |
| **OTel logs** | the same record with `Body` still the template `Hello there, {Name}!`, `Name` as a structured attribute, `EventName` `Greeted`, and `JobName` / `RunId` / `InstanceId` as scope attributes |
| **OTel traces** | a `cadence.job` span, tagged `job.name` / `job.run_id` / `job.trigger` / `job.scheduled_for` / `job.status`, carrying a `cadence.job.progress` event |
| **OTel metrics** | `cadence.runs`, `cadence.run.duration`, `cadence.job.seconds_since_success` |
| **Run history** | the dashboard's sink — in-memory here, since the dashboard itself is v0.4 |

Verified output from a real run:

```
Activity.DisplayName:        cadence.job
Activity.Duration:           00:00:00.2610805
Activity.Tags:
    job.name: hello-there
    job.run_id: f12729e6-f997-4d28-b5a7-ee85d23cfa8a
    job.trigger: Schedule
    job.scheduled_for: 2026-08-24T09:25:50.0000000+00:00
    job.status: Succeeded
Activity.Events:
    cadence.job.progress [2026-08-24 09:25:50 +00:00]
        message: greeted Tony
        data.name: Tony
```

### What it does not show

Clustering. This sample uses the in-memory stores and the no-op coordinator, deliberately: it exists
to prove the telemetry fan-out on the zero-infrastructure path, and adding SQL to it would blur both
purposes. For clustering, see the next sample.

## Cadence.Sample.AppHost

.NET Aspire orchestrating a SQL Server container and **three replicas of one web host**. Each
replica runs the tick loop, claims occurrences out of the shared database, *and* serves the v0.3
control surface — `Cadence.Core`, `Cadence.Storage.Sql` and `Cadence.Api` composed into a single
application, scaled by running more copies of it. That host is `Cadence.Sample.ClusteredWorker`;
the AppHost is only what launches three of it.

**Why not a worker tier and an API tier.** `IJobTrigger.TriggerAsync` ends in
`JobExecutor.DispatchAsync`, so a triggered run executes in the process that received the request.
There is no cross-process dispatch — that would be a queue, and §7 #1 and #4 of the design plan cut
queues on purpose. An API host over a different process's jobs therefore answers every trigger with
`urn:cadence:problem:job-not-found`, which is exactly what an earlier version of this sample did.
Design plan §13.6 states the conclusion: *the supported shape is every replica mapping the API*.

```powershell
./scripts/pack.ps1                              # build the local feed
dotnet run --project samples/Cadence.Sample.AppHost
```

Docker must be running; Aspire starts `mcr.microsoft.com/mssql/server:2022-latest` for you. The
AppHost prints a dashboard URL with a login token — that dashboard is the UI here. Cadence tags
every log record, span and metric with `JobName`, `RunId` and `InstanceId`, so "which replica ran
what" is answerable without `Cadence.Dashboard`, which is v0.4.

Two jobs run on every replica:

| Job | Cron | Triggers | What it is for |
|---|---|---|---|
| `tick-tock` | every 5s | `Schedule, Api` | one span per `job.scheduled_for` however many replicas are up — and the only job an HTTP trigger can start |
| `slow-sweep` | every 10s, takes 25s | `Schedule` | overruns its own slot, on purpose; refuses an API trigger, so the 400 is reachable |

### The endpoint is proxied, and that is the point

Aspire puts one proxy in front of the three replicas and picks the port itself. **There is no fixed
port** — three replicas cannot all bind one, and the spread across replicas you get in exchange is
this sample's most useful demonstration. Read the port off the `worker` resource in the dashboard:

```powershell
$b = 'http://localhost:54602'    # yours will differ
$t = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
```

`curl.exe` ships with Windows and prints non-2xx bodies. `Invoke-RestMethod` throws on them, and a
401, a 404 and a 409 are half of what is worth seeing here. Every response below is from a real run.

The token is `Cadence:Api:Tokens` in the worker's `appsettings.Development.json` — 64 hex characters
of `deadbeef`, the right *shape* and obviously not a secret. A real deployment generates one with
`openssl rand -hex 32`: a pause records its caller as `token:{first 8 hex of the token's SHA-256}`,
readable by anyone who can read `GET /pause`, so a guessable token can be confirmed offline without
touching the service.

#### 1. The gate, in two requests

```powershell
curl.exe -s -i "$b/cadence/api/jobs"
curl.exe -s -i -H "Authorization: Bearer $t" "$b/cadence/api/jobs"
```

```
HTTP/1.1 401 Unauthorized
Content-Length: 0
```

```json
[{"name":"tick-tock","cron":"*/5 * * * * *","timeZone":"UTC","enabled":true,"allowedTriggers":"Schedule, Api","nextOccurrenceUtc":"2026-08-27T06:05:40+00:00","lastRun":{"runId":"80c7d36c-0048-4e5b-9c16-c8c37d8c3ebe","jobName":"tick-tock","status":"Succeeded","trigger":"Schedule","instanceId":"worker-ndentyrm","scheduledForUtc":"2026-08-27T06:05:35+00:00","startedAtUtc":"2026-08-27T06:05:35.889+00:00","completedAtUtc":"2026-08-27T06:05:36.099+00:00","duration":"00:00:00.2020000"}},{"name":"slow-sweep","cron":"*/10 * * * * *","timeZone":"UTC","enabled":true,"allowedTriggers":"Schedule","nextOccurrenceUtc":"2026-08-27T06:05:40+00:00","lastRun":{"runId":"1ddae914-7a27-458c-8438-a776df4dc685","jobName":"slow-sweep","status":"Skipped","trigger":"Schedule","instanceId":"worker-ndentyrm","scheduledForUtc":"2026-08-27T06:05:30+00:00","startedAtUtc":"2026-08-27T06:05:30.903+00:00","completedAtUtc":"2026-08-27T06:05:30.903+00:00","duration":"00:00:00"}}]
```

The tree is policied as a group, so every route on it answers 401 without a token. A *wrong* token
is also 401, not 403 — it never becomes an authenticated identity, so authorization is never
reached. There is no `WWW-Authenticate` header on the way out; nothing in Cadence promises one.

Note `"instanceId":"worker-ndentyrm"` in a response served by whichever replica the proxy picked.
History is the cluster's; the registry is the process's.

#### 2. Triggering a job the cluster actually runs

```powershell
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/tick-tock/trigger"
```

```
HTTP/1.1 202 Accepted
Content-Type: application/json; charset=utf-8

{"runId":"21ef5774-84f6-4bf8-afc8-d892117c1a2d","jobName":"tick-tock","instanceId":"worker-ndentyrm"}
```

202 and not 201: the run has been *accepted*, and there is no resource to point at yet.

This is the request that answered 404 in the previous shape, where the API was a separate process
registering three jobs of its own and the replicas ran two others. Nothing in the database tells one
host about another's code, and nothing hands a run across processes — so an API host that does not
register `tick-tock` cannot start `tick-tock`, ever.

#### 3. The ingress picks the instance, not Cadence

§13.6's first consequence, and the thing the old shape could not show at all. Six triggers through
the one proxied endpoint, half a second apart:

```powershell
1..6 | ForEach-Object { curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/tick-tock/trigger"; Start-Sleep -Milliseconds 500 }
```

```json
{"runId":"3d45823b-01a0-4960-a326-9c6780dce82a","jobName":"tick-tock","instanceId":"worker-ehswtftx"}
{"runId":"ef9753ae-f4d3-405a-846d-e818cbf206ef","jobName":"tick-tock","instanceId":"worker-vecqzbte"}
{"runId":"123d8acb-f77d-4ba8-ab25-3b77fb371742","jobName":"tick-tock","instanceId":"worker-ndentyrm"}
{"runId":"ba60120c-aea5-4fdb-85d5-78d8ec6b17e9","jobName":"tick-tock","instanceId":"worker-ndentyrm"}
{"type":"urn:cadence:problem:run-skipped","title":"No run was started","status":409,"detail":"'tick-tock' was not started: A run of 'tick-tock' is already in flight on this instance and the overlap policy is Skip."}
{"runId":"5359d201-7faf-4ea9-9231-078b175cb24d","jobName":"tick-tock","instanceId":"worker-ndentyrm"}
```

Three different replicas, one unchanged request. Nothing chose them but the proxy — and it assigns
per connection rather than per request, so run it again and the grouping changes. *Different
replicas* is what holds; a rotation you can predict is not.

The 409 is the same lesson from the other side: the fifth trigger landed on a replica whose 5-second
occurrence was mid-flight, and `Skip` is evaluated **on this instance**, per process, not across the
cluster.

The contrast with scheduled runs is the whole thing in one table. Same job, same window:

```powershell
$runs = (curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs?job=tick-tock&limit=500" | ConvertFrom-Json).runs
$runs | Group-Object trigger, instanceId | Select-Object Count, Name | Sort-Object Name
```

```
Count Name
----- ----
    7 Api, worker-ehswtftx
    9 Api, worker-ndentyrm
    7 Api, worker-vecqzbte
  224 Schedule, worker-ndentyrm
    2 Schedule, worker-vecqzbte
```

Claiming elects one replica and keeps it. Triggering goes wherever the request landed.

#### 4. One run per occurrence, still

The guarantee this sample has always existed to show, unchanged by the API being here. Count the
scheduled runs, then count the slots they claim:

```powershell
$scheduled = $runs | Where-Object trigger -eq 'Schedule'
"scheduled runs: {0}   distinct slots: {1}" -f $scheduled.Count,
  ($scheduled | Select-Object -ExpandProperty scheduledForUtc -Unique).Count
```

```
scheduled runs: 226   distinct slots: 226
```

Equal, and they stay equal — through a pause, through the database being stopped and started under
them, and through every trigger above. Three replicas all evaluate the same schedule every second
and all three try to claim; the unique index on `CadenceJobRun` picks one. The same thing is visible
in the dashboard: filter traces to `cadence.job` and group by `job.scheduled_for`.

**The winner does not rotate — and that is not load balancing.** 224 of those 226 slots went to one
replica. Whichever replica started first has its tick phase a
few tens of milliseconds ahead, and that is the whole race. The other two are failover capacity, not
a share of the work. If you came here expecting three replicas to divide the load three ways, this
is the sample correcting you — and §14.1 of the design plan is the architecture that would change
it, recorded and deliberately not built.

The other 2 slots are the leader being *disturbed*, not the load spreading: one during the pause
below, while the leader had stopped ticking and a replica had not yet polled the change, and one
when the database came back after being stopped. Interrupt the leader and someone else wins the next
slot immediately, which is the failover half of the same mechanism.

**The `Skip` caveat, live.** `slow-sweep` takes longer than the gap between its occurrences. Within
one replica `Skip` is strict, so you will see runs recorded as `Skipped`. Across replicas it cannot
be, because the claim answers *"has anyone started this slot?"* and not *"is anyone running this
job?"* — so once the leader is busy, a different replica claims the next slot and starts while the
first is still going. Two overlapping spans, same `job.name`, different `job.instance_id`. A job
needing a hard cross-instance guarantee has to take its own lock.

**The janitor reaping a dead replica.** Kill the winning replica *hard* — Task Manager, or
`Stop-Process -Id <pid> -Force` — while a `slow-sweep` is in flight. Do not use the dashboard's Stop
button: that is a graceful shutdown, so Cadence drains, records the run as `Aborted`, and deletes its
registry row. There is nothing for the janitor to find.

After a hard kill the heartbeat simply stops. Within `HeartbeatTimeout` plus one janitor pass the run
flips from `Running` to `Lost` — a distinct status meaning *nobody recorded an outcome at all*, which
is a different conversation with an operator than `Aborted`. Meanwhile the next-earliest replica
starts winning claims immediately, and Aspire's proxy stops routing to the dead one.

Measured on the run this sample was written against: killed at 08:23:35 mid-sweep, marked `Lost` at
08:23:56, and the surviving replica had taken over by the next occurrence at 08:23:40.

#### 5. Reading history, and the progress a run leaves behind

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs?job=tick-tock&limit=2"
```

```json
{"runs":[{"runId":"011441a9-2464-4265-804f-c733e6754557","jobName":"tick-tock","status":"Succeeded","trigger":"Schedule","instanceId":"worker-ndentyrm","scheduledForUtc":"2026-08-27T06:17:00+00:00","startedAtUtc":"2026-08-27T06:17:00.895+00:00","completedAtUtc":"2026-08-27T06:17:01.117+00:00","duration":"00:00:00.2140000"},{"runId":"26fcdfc4-c27d-4975-9804-5c9091eb0968", ...}],"limit":2,"offset":0}
```

`limit` comes back so a caller can see what clamping happened; ask for 10000 and you get 500. Filter
by `job`, `status`, `from`, `to`, `instance`, `limit` and `offset`. An API trigger is recorded as
`Api` — not `Manual`, which is reserved for a dashboard button or an in-process call — and a
triggered run carries no `scheduledForUtc` at all, because it belongs to no occurrence.

The by-id read is the only place progress appears, because a list view renders none and fetching it
would be a second query per row:

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs/6fc5c966-ecce-44a4-9d86-ba8a60fa5fc8"
```

```json
{"run":{"runId":"6fc5c966-ecce-44a4-9d86-ba8a60fa5fc8","jobName":"tick-tock","status":"Succeeded","trigger":"Api","instanceId":"worker-ehswtftx","startedAtUtc":"2026-08-27T06:09:23.936+00:00","completedAtUtc":"2026-08-27T06:09:24.15+00:00","duration":"00:00:00.2040000"},"log":[{"timestampUtc":"2026-08-27T06:09:23.945+00:00","message":"claimed and ran"}]}
```

That entry is the job's `context.Report(...)` call, read back out of a database three processes
write to.

#### 6. Pause: one HTTP call, every replica

Pause is store-backed, so the request only has to reach *a* replica. Hold the schedule and leave
triggers open — the shape of an actual incident:

```powershell
curl.exe -s -i -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" `
  -d '{"scope":"Schedule","reason":"payment gateway incident"}' "$b/cadence/api/pause"
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/pause"
```

```
HTTP/1.1 204 No Content
```

```json
{"scope":"Schedule","reason":"payment gateway incident","setBy":"token:247d08f3","setAtUtc":"2026-08-27T06:07:12.942+00:00"}
```

`setBy` is read from the authenticated principal, not from the request body — an audit trail cannot
be written by the caller being audited. `token:247d08f3` is the first 8 hex of this token's SHA-256,
stable across restarts.

Watch the last scheduled occurrence stop moving. `tick-tock` is due every 5 seconds:

```
06:07:12.9  PUT /pause {"scope":"Schedule"}  -> 204
06:07:15    one last occurrence, on worker-vecqzbte      <- had not polled yet
06:07:45    latest scheduled run: 06:07:15
06:08:05    latest scheduled run: 06:07:15
```

Two things to read there. The straggler at `06:07:15` ran on a *different* replica from the leader,
which is the `SchedulePollInterval` — 5 seconds in this sample — visible as a real propagation
delay. And then nothing: had the pause reached only the replica that served the PUT, one of the
other two would have started winning the leader's abandoned slots. Silence on all three is the
propagation.

Triggers stay open the whole time, because the two switches are independent:

```json
{"runId":"8eaf81f7-8090-49ad-9cd3-8ddbd34d2cb5","jobName":"tick-tock","instanceId":"worker-ndentyrm"}
{"runId":"616b3bcc-141c-40e4-822f-945da20c5975","jobName":"tick-tock","instanceId":"worker-ehswtftx"}
```

Close the other switch and the escape hatch shuts too. Give it a poll interval, then four triggers
through the same proxied endpoint:

```powershell
curl.exe -s -o NUL -w "pause: %{http_code}`n" -X PUT -H "Authorization: Bearer $t" `
  -H "Content-Type: application/json" -d '{"scope":"All","reason":"payment gateway incident"}' "$b/cadence/api/pause"
Start-Sleep -Seconds 7
1..4 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code} " -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/tick-tock/trigger" }
```

```
pause: 204
409 409 409 409
```

Four in a row, on connections the proxy hands out across replicas: whichever ones answered had all
seen the pause.

```json
{"type":"urn:cadence:problem:scheduler-paused","title":"Triggers are paused","status":409,"detail":"'tick-tock' was not started because triggers are paused by token:247d08f3: payment gateway incident"}
```

Who and why, in the body, to the caller being refused — which is why the fingerprint is recorded
rather than the pause being anonymous. `{"scope":"None"}` reopens both, and `GET /pause` afterwards
still carries the `setBy` and `setAtUtc` of whoever resumed: "nobody paused anything" and "somebody
resumed" are different facts.

Single quotes around the JSON: PowerShell 7 passes that through to `curl.exe` intact, where the
`-d '{\"scope\":...}'` form that PowerShell 5.1 needs sends literal backslashes instead.

#### 7. The refusals, as problem documents

```powershell
curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/no-such-job/trigger"
curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/slow-sweep/trigger"
curl.exe -s -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" -d '{"scope":"Everything"}' "$b/cadence/api/pause"
```

```json
{"type":"urn:cadence:problem:job-not-found","title":"Job not found","status":404,"detail":"No job is registered under the name 'no-such-job'."}
{"type":"urn:cadence:problem:trigger-not-allowed","title":"Trigger not allowed","status":400,"detail":"'slow-sweep' cannot be triggered by Api. 'slow-sweep' allows Schedule."}
{"type":"urn:cadence:problem:invalid-pause-scope","title":"Unknown pause scope","status":400,"detail":"'Everything' is not a pause scope. Use None, Schedule, Triggers or All."}
```

404 for a name that does not exist, 400 for one that does but declares no `Api` trigger — no amount
of retrying turns the second into a 202, which is what makes it that shape of 4xx. Every one is
`application/problem+json` with a `urn:cadence:problem:` type, so a caller can branch on the type
rather than parsing prose. `GET /runs/{id}` for an unknown id answers `urn:cadence:problem:run-not-found`
the same way.

The 404 is also how a misconfigured pod diagnoses itself. `MapCadenceApi()` does not throw on an
empty registry — registering jobs behind a feature flag is legitimate — it warns at map time, and
the trigger endpoint names the cause in the body.

One caveat: a *malformed* body is not Cadence's 400 but ASP.NET Core's, and in `Development` that
means the developer exception page, which echoes the request headers, your `Authorization` line
included. Not a reason to avoid it; a reason not to paste that output anywhere.

#### 8. Health, including a store that is down

```powershell
curl.exe -s -i "$b/health/live"                    # no token
curl.exe -s -i "$b/cadence/api/health/storage"     # no token
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/health/storage"
```

```
HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

```
HTTP/1.1 401 Unauthorized
```

```json
{"status":"Healthy","checks":[{"name":"cadence-sql","status":"Healthy","description":"The schedule database answered.","duration":"00:00:00.0046067"}]}
```

`/health/live` and `/health/ready` are anonymous because a kubelet cannot present a token.
`/cadence/api/health/storage` is inside the group and behind the gate, because it reports the last
store error — operator information, not a probe.

Now take the database away — `docker stop` the `sql` container, or stop the resource from the
dashboard — and ask the same three routes:

```
/health/live               200 Healthy
/health/ready              200
/cadence/api/health/storage
{"status":"Degraded","checks":[{"name":"cadence-sql","status":"Degraded","description":"The schedule
 database did not answer.","error":"Connection Timeout Expired.  The timeout period elapsed while
 attempting to consume the pre-login handshake acknowledgement. ...","duration":"00:00:11.7455250"}]}
```

**That is the split working.** The store is gone, the storage report says so and names the error,
and liveness stays 200 — because a storage blip that turned into a 503 on every replica at once
would have the orchestrator restart the entire cluster during exactly the incident it should be
riding out. Start the container again and the next check answers `Healthy` with no restart.

#### 9. Swagger, and the Authorize button

`Development` gets an OpenAPI document at `/openapi/v1.json` and Swagger UI over it at `/swagger`.
The document comes from the framework's `AddOpenApi()`; `Cadence.Api` declares each route's statuses
and response shapes itself, so the schemas are the real ones.

**Authorize** takes the token above — paste the value alone, Swagger UI adds the `Bearer` prefix:

```json
{"CadenceToken":{"type":"http","description":"A token from Cadence:Api:Tokens or CADENCE_API_TOKEN. Paste the token alone; Swagger UI adds the Bearer prefix.","scheme":"bearer"}}
```

That scheme comes from `OpenApiSecurity.cs` in this sample, not from the package: describing a
security scheme means referencing `Microsoft.AspNetCore.OpenApi`, and `Cadence.Api` will not put
that dependency on hosts that generate no document. Any host that wants the same button copies that
file. The padlock is added per operation, from the authorization metadata `MapCadenceApi()` already
stamped, so the gate's branches are read rather than re-decided.

#### 10. The mapping gate refusing to start

The gate is a *startup* failure, on purpose: an operator meets it on deploy rather than a stranger
meeting the open endpoint. `--no-launch-profile` drops the `Development` the launch profile sets,
and `Production` does not load `appsettings.Development.json`, so no token is configured:

```powershell
$env:ConnectionStrings__cadence = 'Server=localhost,1;Database=cadence;User Id=sa;Password=nope'
dotnet run --project samples/Cadence.Sample.ClusteredWorker --no-launch-profile -- --environment Production
```

```
Unhandled exception. Cadence.CadenceStartupException: MapCadenceApi() refuses to map outside
Development because nothing would authenticate it. Supply a token (CADENCE_API_TOKEN, or
Cadence:Api:Tokens), or name an authorization policy with CadenceApiOptions.RequireAuthorization,
or — if something in front of this application already authenticates callers — set
CadenceApiOptions.AllowUnauthenticated.
   at Cadence.Api.CadenceApiEndpointExtensions.MapCadenceApi(IEndpointRouteBuilder endpoints)
```

The process exits without ever listening, and it does so before the storage tier is contacted at all
— which is why the connection string above only has to exist, not to work. Satisfy the gate with
`$env:CADENCE_API_TOKEN = '<64 hex characters>'` and the same command gets past the map, logging
which source supplied how many tokens and no value:

```
info: Cadence.Api[3002] Cadence's API accepted 1 token(s): 0 set in code, 0 from
      Cadence:Api:Tokens, 1 from CADENCE_API_TOKEN. Values are never logged.
```

That is the line to read when a token that ought to work does not.

### Editing a schedule by hand

The dashboard that would do this properly is v0.4, so for now it is SQL — and there is one trap worth
knowing. `CadenceJobSchedule` holds **overrides**, not a seeded copy of what the code declared, so a
job running on its `[ScheduledJob]` cron has no row yet: you `INSERT`, you do not `UPDATE`.

The second half matters more. Replicas do not poll the schedule table; they poll a single-row version
counter, which is what keeps "nothing changed" cheap. `UpsertAsync` bumps it in the same transaction.
A hand-written statement has to bump it too, or the edit sits in the table and nothing ever reads it:

```sql
BEGIN TRANSACTION;
INSERT INTO CadenceJobSchedule (JobName, CronExpression, TimeZoneId, Enabled, UpdatedAtUtc, UpdatedBy)
VALUES ('tick-tock', '*/20 * * * * *', 'UTC', 1, SYSUTCDATETIME(), 'me');
UPDATE CadenceScheduleVersion SET Version = Version + 1 WHERE Id = 1;
COMMIT TRANSACTION;
```

All three replicas pick it up within `SchedulePollInterval`, which this sample shortens to 5 seconds.
Schedule writes are not on the API tree at all, and no sample can show them: a token may start work
and stop it, while only a person changes *when* work happens.

### Timings are demo values, not defaults

The worker sets `HeartbeatInterval` 5s, `HeartbeatTimeout` 20s, `JanitorInterval` 15s and
`SchedulePollInterval` 5s. The real defaults are 15s / 60s / 5min / 10s, which are right for a
deployment and wrong for standing in front of a screen — the janitor demo would take five minutes.
The relationship the defaults encode is preserved: the timeout is still four heartbeats, so one
missed beat never gets a live replica's runs reaped out from under it.

### It consumes Cadence as packages too

Like `Cadence.Sample.Worker`, and for the same reason. This is also the only thing in the repository
that consumes `Cadence.Storage.Sql` and `Cadence.Api` as NuGet packages rather than by project
reference, so it is the check that those tiers pack correctly at all. Run `./scripts/pack.ps1` first,
or the restore fails.

The one exception is the AppHost's project reference to the worker, which is not consumption — it is
how Aspire is told which project to launch, and what generates the `Projects.*` type.

### Running it without any infrastructure

There is no separate zero-infrastructure sample, because it is an edit rather than a project. Delete
the `UseSqlStorage(...)` call and the connection-string lookup above it from
`Cadence.Sample.ClusteredWorker/Program.cs` and `AddCadence` falls back to the in-memory stores and
the no-op coordinator. `dotnet run` on the project alone then boots with no Docker and no database,
and the whole control surface is still mounted on `http://localhost:5000`:

```
info: Cadence.Api[3002] Cadence's API accepted 1 token(s): 0 set in code, 1 from
      Cadence:Api:Tokens, 0 from CADENCE_API_TOKEN.
info: Cadence.Scheduling.CadenceHostedService[1004] Cadence started on instance
      SEBASTIANS:29648:9642c7ee with 2 job(s), ticking every 00:00:01.
```

What you lose is everything this sample is about: run history becomes a per-process ring that empties
on restart, every `instanceId` in every response is the same string, and there is no occurrence for
anyone else to lose the race for.

### What it still cannot show

A history view and schedule editing in a UI. Both are `Cadence.Dashboard`, which is v0.4. The Aspire
dashboard covers live telemetry — logs, traces, metrics, per replica — but it reads OTel, not
`CadenceJobRun`, so "what ran last Tuesday" is still a SQL query.

The loopback filter, too. It engages on one branch of the gate — `Development`, no token, no policy,
no `AllowUnauthenticated` — and reaching it means starting a host with the token blanked and bound
off loopback, which is not a state this sample can be in while three replicas are talking to a
database. Design plan §13.3 documents the branch and the 403 it answers with.

**The guarantee is also proven without any of this.** `ClusteredSchedulingTests` runs five instances
against a Testcontainers SQL Server with a fake clock and asserts one run per occurrence, on every CI
build, deterministically. This sample exists for what a test cannot do: real processes, real kills,
real restarts, and something to look at.
