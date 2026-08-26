# Samples

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

.NET Aspire orchestrating a SQL Server container and **three replicas of one worker**, all claiming
occurrences out of the same database. This is the sample that shows the guarantee on the README's
first screen — *at most one instance starts a given occurrence* — happening between real processes.

```powershell
./scripts/pack.ps1                              # build the local feed
dotnet run --project samples/Cadence.Sample.AppHost
```

Docker must be running; Aspire starts `mcr.microsoft.com/mssql/server:2022-latest` for you. The
AppHost prints a dashboard URL with a login token — that dashboard is the UI here. Cadence tags every
log record, span and metric with `JobName`, `RunId` and `InstanceId`, so "which replica ran what" is
answerable without `Cadence.Dashboard`, which is v0.4 and would add schedule editing and a history
view rather than visibility.

Two jobs run:

| Job | Cron | What it is for |
|---|---|---|
| `tick-tock` | every 5s | one span per `job.scheduled_for`, no matter how many replicas are up |
| `slow-sweep` | every 10s, takes 25s | overruns its own slot, on purpose |

### The four things worth watching

**1. One run per occurrence.** Filter the dashboard's traces to `cadence.job` and group by
`job.scheduled_for`. Every slot has exactly one span. Three replicas are all evaluating the same
schedule every second and all three try to claim; the unique index on `CadenceJobRun` picks one.

**2. The winner never rotates — and that is not load balancing.** One replica wins *every*
occurrence and the other two record nothing at all, until the winner dies. Whichever replica started
first has its tick phase a few tens of milliseconds ahead, and that is the whole race. Claiming an
occurrence is a correctness mechanism; the other replicas are failover capacity, not a share of the
work. If you came here expecting three replicas to divide the load three ways, this is the sample
correcting you.

**3. The `Skip` caveat, live.** `slow-sweep` takes longer than the gap between its occurrences.
Within one replica `Skip` is strict, so you will see runs recorded as `Skipped`. Across replicas it
cannot be, because the claim answers *"has anyone started this slot?"* and not *"is anyone running
this job?"* — so once the leader is busy, a different replica claims the next slot and starts while
the first is still going. Two overlapping spans, same `job.name`, different `job.instance_id`. That
is documented behaviour, not a bug; a job needing a hard cross-instance guarantee has to take its own
lock.

**4. The janitor reaping a dead replica.** Kill the winning replica *hard* — Task Manager, or
`Stop-Process -Id <pid> -Force` — while a `slow-sweep` is in flight. Do not use the dashboard's Stop
button: that is a graceful shutdown, so Cadence drains, records the run as `Aborted`, and deletes its
registry row. There is nothing for the janitor to find.

After a hard kill the heartbeat simply stops. Within `HeartbeatTimeout` plus one janitor pass the run
flips from `Running` to `Lost` — a distinct status meaning *nobody recorded an outcome at all*, which
is a different conversation with an operator than `Aborted`. Meanwhile the next-earliest replica
starts winning claims immediately.

Measured on the run this sample was written against: killed at 08:23:35 mid-sweep, marked `Lost` at
08:23:56, and the surviving replica had taken over by the next occurrence at 08:23:40.

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

### Timings are demo values, not defaults

The worker sets `HeartbeatInterval` 5s, `HeartbeatTimeout` 20s, `JanitorInterval` 15s and
`SchedulePollInterval` 5s. The real defaults are 15s / 60s / 5min / 10s, which are right for a
deployment and wrong for standing in front of a screen — the janitor demo would take five minutes.
The relationship the defaults encode is preserved: the timeout is still four heartbeats, so one
missed beat never gets a live replica's runs reaped out from under it.

### It consumes Cadence as a package too

Like `Cadence.Sample.Worker`, and for the same reason. This is also the first thing anywhere in the
repository that consumes `Cadence.Storage.Sql` as a NuGet package rather than by project reference,
so it is the first check that the SQL tier packs correctly at all. Run `./scripts/pack.ps1` first, or
the restore fails.

The one exception is the AppHost's reference to the worker project, which is not consumption — it is
how Aspire is told which project to launch, and what generates the `Projects.*` type.

### What it still cannot show

A history view and schedule editing in a UI. Both are `Cadence.Dashboard`, which is v0.4. The Aspire
dashboard covers live telemetry — logs, traces, metrics, per replica — but it reads OTel, not
`CadenceJobRun`, so "what ran last Tuesday" is still a SQL query.

**The guarantee is also proven without any of this.** `ClusteredSchedulingTests` runs five instances
against a Testcontainers SQL Server with a fake clock and asserts one run per occurrence, on every CI
build, deterministically. This sample exists for what a test cannot do: real processes, real kills,
real restarts, and something to look at.

## Cadence.Sample.Api

A minimal `WebApplication` that mounts the v0.3 control surface over the in-memory stores. The two
worker samples have no HTTP server, so before this one there was nowhere for `MapCadenceApi()` to
mount and nothing in the repository that drove the endpoints, the token scheme or the health split
by hand. That is all this sample is for.

```powershell
./scripts/pack.ps1                               # build the local feed
dotnet run --project samples/Cadence.Sample.Api  # ctrl+c to stop
```

It listens on `http://localhost:5233` (`Urls` in `appsettings.json`) and
`Properties/launchSettings.json` puts it in `Development`, which is what lets the token in
`appsettings.Development.json` be found at all.

Three jobs exist only so the endpoints have something to talk about:

| Job | Cron | Triggers | Why it is here |
|---|---|---|---|
| `inventory-sweep` | `*/15 * * * * *` | `Schedule, Api` | fills history on its own, and reports three progress entries per run |
| `reindex-catalog` | none | `Api, Manual` | the trigger-only shape — no cron, no next occurrence |
| `nightly-report` | `0 3 * * *` | `Schedule` | refuses an API trigger, so the 400 is reachable |

**In-memory stores, no OpenTelemetry.** The same reasoning as `Cadence.Sample.Worker`, applied in
the other direction: that sample owns the telemetry story and the AppHost owns SQL, so adding either
here would blur what this one shows. The visible cost is `GET /health/storage` answering `Healthy`
over an empty list of checks, which is worth seeing in its own right — see below.

### The token

`Cadence:Api:Tokens` in `appsettings.Development.json`, not a constant in `Program.cs`, so the
sample exercises the path a deployment actually uses. The value is 64 hex characters of `deadbeef` —
the right *shape* and obviously not a secret.

**A real deployment generates one with `openssl rand -hex 32`.** A pause records its caller as
`token:{first 8 hex of the token's SHA-256}`, readable by anyone who can read `GET /pause`, so a
guessable token can be confirmed offline without ever touching the service.

### Driving it

Everything below uses `curl.exe`, which ships with Windows. `Invoke-RestMethod` works too, but it
throws on any non-2xx — and a 401, a 404 and a 409 are half of what is worth seeing here, so you
would be reading `$_.Exception.Response` rather than a body. Set these once:

```powershell
$b = 'http://localhost:5233'
$t = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
```

Every response below was captured from a real run of this sample.

#### 1. The gate, in two requests

```powershell
curl.exe -s -i "$b/cadence/api/jobs"
curl.exe -s -i -H "Authorization: Bearer $t" "$b/cadence/api/jobs"
```

```
HTTP/1.1 401 Unauthorized
Content-Length: 0
```

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
```

That is the whole point of `AddApi()` plus a configured token: the tree is policied as a group, so
every route on it answers 401 without one. A *wrong* token is also 401, not 403 —
`Authorization: Bearer not-the-token` never becomes an authenticated identity, so authorization is
never reached. Note there is no `WWW-Authenticate` header on the way out; nothing in Cadence
promises one, but a client looking for it will not find it.

#### 2. Jobs, in both shapes

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/jobs"
```

```json
[{"name":"inventory-sweep","cron":"*/15 * * * * *","timeZone":"UTC","enabled":true,"allowedTriggers":"Schedule, Api","nextOccurrenceUtc":"2026-08-26T13:50:45+00:00","lastRun":{"runId":"90ae4e06-0135-48f2-b2d7-3485d3d53bb0","jobName":"inventory-sweep","status":"Succeeded","trigger":"Schedule","instanceId":"sample-api:47044","scheduledForUtc":"2026-08-26T13:50:30+00:00","startedAtUtc":"2026-08-26T13:50:30.7927646+00:00","completedAtUtc":"2026-08-26T13:50:31.2692652+00:00","duration":"00:00:00.4763870"}},{"name":"reindex-catalog","timeZone":"UTC","enabled":true,"allowedTriggers":"Api, Manual"},{"name":"nightly-report","cron":"0 3 * * *","timeZone":"UTC","enabled":true,"allowedTriggers":"Schedule","nextOccurrenceUtc":"2026-08-27T03:00:00+00:00"}]
```

`reindex-catalog` carries no `cron` and no `nextOccurrenceUtc` at all — the fields are absent rather
than null. That is the trigger-only shape, and it is why the sample registers one.

Job detail adds the effective policy and the recent runs:

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/jobs/inventory-sweep"
```

```json
{"job":{"name":"inventory-sweep", ...},"overlap":"Skip","maxDuration":"00:00:30","settings":{},"recentRuns":[{"runId":"90ae4e06-...","status":"Succeeded","trigger":"Schedule", ...},{"runId":"ff842e54-...","scheduledForUtc":"2026-08-26T13:50:15+00:00", ...}]}
```

`settings` is empty because there is no writable schedule source here — with the in-memory source
the code-declared defaults are all there is.

#### 3. Triggering, and the log a run leaves behind

```powershell
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/reindex-catalog/trigger"
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/inventory-sweep/trigger"
```

```
HTTP/1.1 202 Accepted

{"runId":"11ebd94d-b598-4a04-a916-58e48b91ba4e","jobName":"reindex-catalog","instanceId":"sample-api:47044"}
```

```
HTTP/1.1 202 Accepted

{"runId":"0d54e202-00a9-495b-8ef8-0e1e634f1db2","jobName":"inventory-sweep","instanceId":"sample-api:47044"}
```

202 and not 201: the run has been *accepted*, and there is no resource to point at yet. `instanceId`
is which process took it, which is the field that matters the moment there is more than one replica.

Both runs are in history immediately, and an API trigger is recorded as `Api` — not `Manual`, which
is reserved for a dashboard button or an in-process call:

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs?limit=3"
```

```json
{"runs":[{"runId":"a3727501-...","jobName":"inventory-sweep","status":"Succeeded","trigger":"Schedule","scheduledForUtc":"2026-08-26T13:50:45+00:00", ...},{"runId":"0d54e202-...","jobName":"inventory-sweep","status":"Succeeded","trigger":"Api","startedAtUtc":"2026-08-26T13:50:42.8320915+00:00", ...},{"runId":"11ebd94d-...","jobName":"reindex-catalog","status":"Succeeded","trigger":"Api", ...}],"limit":3,"offset":0}
```

A triggered run has no `scheduledForUtc` — it belongs to no occurrence — and the scheduled one does.
`limit` comes back so a caller can see what clamping happened; ask for 10000 and you get 500.

The by-id read is the only place progress appears, because a list view renders none and fetching it
would be a second query per row:

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs/11ebd94d-b598-4a04-a916-58e48b91ba4e"
```

```json
{"run":{"runId":"11ebd94d-b598-4a04-a916-58e48b91ba4e","jobName":"reindex-catalog","status":"Succeeded","trigger":"Api","instanceId":"sample-api:47044","startedAtUtc":"2026-08-26T13:50:42.8126832+00:00","completedAtUtc":"2026-08-26T13:50:43.009222+00:00","duration":"00:00:00.1964377"},"log":[{"timestampUtc":"2026-08-26T13:50:42.8131813+00:00","message":"rebuilding the index"},{"timestampUtc":"2026-08-26T13:50:43.0091679+00:00","message":"index swapped in"}]}
```

Those two entries are the job's `context.Report(...)` calls. Filter the list by job with
`?job=reindex-catalog`, and by `status`, `from`, `to`, `instance`, `limit` and `offset`.

#### 4. Pause, and who is recorded as having done it

```powershell
curl.exe -s -i -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" `
  -d '{"scope":"Triggers","reason":"payment gateway incident","setBy":"mallory"}' "$b/cadence/api/pause"
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/pause"
```

```
HTTP/1.1 204 No Content
```

```json
{"scope":"Triggers","reason":"payment gateway incident","setBy":"token:247d08f3","setAtUtc":"2026-08-26T13:51:21.899701+00:00"}
```

**The `"setBy":"mallory"` in that body went nowhere.** `setBy` is read from the authenticated
principal and the request shape has no such field, so an audit trail cannot be written by the caller
being audited. `token:247d08f3` is the first 8 hex of this token's SHA-256 — stable across restarts,
so the same caller attributes to the same string every time.

Single quotes around the JSON: PowerShell 7 passes that through to `curl.exe` intact, where the
`-d '{\"scope\":...}'` form that PowerShell 5.1 needs sends literal backslashes instead.

Now the refusal:

```powershell
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/reindex-catalog/trigger"
```

```
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{"type":"urn:cadence:problem:scheduler-paused","title":"Triggers are paused","status":409,"detail":"'reindex-catalog' was not started because triggers are paused by token:247d08f3: payment gateway incident"}
```

Who and why, in the body, to the caller being refused — which is the reason the fingerprint is
recorded rather than the pause being anonymous.

The two switches really are independent. `{"scope":"Schedule"}` holds the tick loop while leaving
triggers open, which is the shape of an actual incident:

```
PUT  /pause {"scope":"Schedule"}    -> 204
POST /jobs/inventory-sweep/trigger  -> 202     # the escape hatch stays open
lastRun.scheduledForUtc = 2026-08-26T13:51:45+00:00
...twenty seconds later, unchanged: 2026-08-26T13:51:45+00:00
```

`{"scope":"None"}` reopens both. `GET /pause` afterwards still carries the `setBy` and `setAtUtc` of
whoever resumed — "nobody paused anything" and "somebody resumed" are different facts.

#### 5. The refusals, as problem documents

```powershell
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/no-such-job/trigger"
curl.exe -s -i -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/nightly-report/trigger"
curl.exe -s -i -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" -d '{"scope":"Everything"}' "$b/cadence/api/pause"
```

```
HTTP/1.1 404 Not Found
Content-Type: application/problem+json

{"type":"urn:cadence:problem:job-not-found","title":"Job not found","status":404,"detail":"No job is registered under the name 'no-such-job'."}
```

```
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"urn:cadence:problem:trigger-not-allowed","title":"Trigger not allowed","status":400,"detail":"'nightly-report' cannot be triggered by Api. 'nightly-report' allows Schedule."}
```

```
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{"type":"urn:cadence:problem:invalid-pause-scope","title":"Unknown pause scope","status":400,"detail":"'Everything' is not a pause scope. Use None, Schedule, Triggers or All."}
```

404 for a name that does not exist, 400 for one that does but declares no `Api` trigger — no amount
of retrying turns the second into a 202, which is what makes it that shape of 4xx. Every one is
`application/problem+json` with a `urn:cadence:problem:` type, so a caller can branch on the type
rather than parsing prose. `GET /runs/{id}` for an unknown id answers
`urn:cadence:problem:run-not-found` the same way.

One caveat on this path: a *malformed* body is not Cadence's 400 but ASP.NET Core's, and in
`Development` that means the developer exception page — which echoes the request headers, your
`Authorization` line included. Not a reason to avoid it here; a reason not to paste that output
anywhere.

#### 6. Health: the access split, in two requests

```powershell
curl.exe -s -i "$b/health/live"                    # no token
curl.exe -s -i "$b/cadence/api/health/storage"     # no token
```

```
HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

```
HTTP/1.1 401 Unauthorized
Content-Length: 0
```

`/health/live` and `/health/ready` are anonymous because a kubelet cannot present a token.
`/cadence/api/health/storage` is inside the group and therefore behind the gate, because it reports
the last store error — operator information, not a probe. With the token:

```json
{"status":"Healthy","checks":[]}
```

An empty list, and `Healthy` anyway. This sample registers no storage package, so there is nothing
tagged `cadence.storage` to ask, and the endpoint reports the worst of no checks rather than
inventing a failure. Point the same route at the AppHost sample and each entry names its store, its
round-trip duration and its last error.

#### 7. The mapping gate refusing to start

The gate is a *startup* failure, on purpose: an operator meets it on deploy rather than a stranger
meeting the open endpoint. `--no-launch-profile` drops the `Development` the launch profile sets, and
`Production` does not load `appsettings.Development.json`, so no token is configured:

```powershell
dotnet run --project samples/Cadence.Sample.Api --no-launch-profile -- --environment Production
```

```
Unhandled exception. Cadence.CadenceStartupException: MapCadenceApi() refuses to map outside
Development because nothing would authenticate it. Supply a token (CADENCE_API_TOKEN, or
Cadence:Api:Tokens), or name an authorization policy with CadenceApiOptions.RequireAuthorization,
or — if something in front of this application already authenticates callers — set
CadenceApiOptions.AllowUnauthenticated.
```

The process exits without ever listening. Satisfy it and the same command starts, with the token
count logged and no value:

```powershell
$env:CADENCE_API_TOKEN = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
dotnet run --project samples/Cadence.Sample.Api --no-launch-profile -- `
  --environment Production --urls http://localhost:5234
```

```
info: Cadence.Api[3002]
      Cadence's API accepted 1 token(s): 0 set in code, 0 from Cadence:Api:Tokens, 1 from
      CADENCE_API_TOKEN. Values are never logged.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Production
```

Which source supplied how many is the line to read when a token that ought to work does not.

#### 8. The loopback filter

The filter engages on exactly one branch of the gate: `Development` with **no** token, no policy and
`AllowUnauthenticated` unset. This sample configures a token, so on the normal `dotnet run` path the
filter is not in the pipeline at all — the policy is. To reach that branch, blank the configured
token from the command line and bind somewhere other than loopback:

```powershell
dotnet run --project samples/Cadence.Sample.Api --no-launch-profile -- `
  --environment Development --Cadence:Api:Tokens:0=' ' --urls http://0.0.0.0:5235
```

A whitespace token is discarded rather than accepted, which is what drops the count to zero. Boot
says so:

```
warn: Cadence.Api[3000]
      Cadence's API is mapped with nothing that would authenticate it. Anything on this host that
      can reach /cadence can trigger jobs and halt scheduling. This is allowed in Development only,
      where non-loopback callers are refused; outside it, MapCadenceApi() will refuse to map.
```

**No token is needed now, and localhost sees no difference** — which is the point, and also why a
plain local `curl` demonstrates nothing. What demonstrates it is a request arriving from a
non-loopback address, and that does not need a second machine: connect to one of *this* machine's own
LAN addresses and the connection's remote address is that address, not `127.0.0.1`.

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "http://127.0.0.1:5235/cadence/api/jobs"
curl.exe -s -i "http://192.168.212.235:5235/cadence/api/jobs"   # this host's own LAN address
```

```
200
```

```
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{"type":"urn:cadence:problem:not-loopback","title":"Loopback callers only","status":403,"detail":"Cadence's API is mapped with nothing that would authenticate it, which is allowed in Development only, so it answers loopback callers alone. Configure a token (CADENCE_API_TOKEN, or Cadence:Api:Tokens), name an authorization policy with CadenceApiOptions.RequireAuthorization, or — if something in front of this application already authenticates callers — set CadenceApiOptions.AllowUnauthenticated."}
```

Substitute your own address from `Get-NetIPAddress -AddressFamily IPv4`. The health probes are
mapped outside the group and stay open — `http://192.168.212.235:5235/health/live` answers `Healthy`
to that same non-loopback caller — because a kubelet is never on loopback and a liveness probe
exposes nothing. That is a container shipped with `ASPNETCORE_ENVIRONMENT=Development` being
embarrassing rather than exploitable.

### What it does not show

Persistence, clustering, telemetry, and schedule writes.

The first three are deliberate: run history here is a per-process ring, so restarting the sample
empties `GET /runs`, and one instance means every `instanceId` in every response is the same string.
`Cadence.Sample.AppHost` is where those responses come from three replicas sharing a database.

Schedule writes are not on this tree at all, and no sample can show them: a token may start work and
stop it, while only a person changes *when* work happens. Editing a schedule is SQL by hand until
the v0.4 dashboard.
