# Samples

| Sample | What it is for |
|---|---|
| `Cadence.Sample.Worker` | The telemetry fan-out, on the zero-infrastructure path. One job, in-memory stores, OTel to the console. |
| `Cadence.Sample.ClusteredWorker` | The deployable shape: Core, a storage tier and the control surface in **one** web host. Launched by one of the two AppHosts, not directly. |
| `Cadence.Sample.AppHost.Sql` | Aspire, a SQL Server container, a Keycloak container, and three replicas of that host behind one proxied endpoint. |
| `Cadence.Sample.AppHost.Redis` | The same three replicas, the same walkthrough, against a Redis container. **The store is the only difference.** |

The last two are a matched pair. They launch the same worker project, register the same three jobs,
mount the same control surface and are driven with the same requests; the only thing that changes is
which connection string the AppHost injects. [What actually differs](#what-actually-differs) is the
short list of things that turned out not to be identical, measured rather than assumed.

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

Progress a job reports fans out to four places at once: **MEL** (`info: ...HelloThereJob[1] Hello
there, Tony!`), **OTel logs** (the same record with `Body` still the template `Hello there, {Name}!`,
`Name` a structured attribute, and `JobName` / `RunId` / `InstanceId` as scope attributes), **OTel
traces**, and **OTel metrics** (`cadence.runs`, `cadence.run.duration`,
`cadence.job.seconds_since_success`). The span is the one worth seeing whole:

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

**What it does not show: clustering.** In-memory stores and the no-op coordinator, deliberately —
adding a store to it would blur both purposes. For clustering, read on.

## The clustered pair

.NET Aspire orchestrating a store and **three replicas of one web host**. Each replica runs the tick
loop, claims occurrences out of the shared store, *and* serves the v0.3 control surface — Core, a
storage tier and `Cadence.Api` composed into a single application, scaled by running more copies of
it. That host is `Cadence.Sample.ClusteredWorker`; an AppHost is only what launches three of it.

**Why not a worker tier and an API tier.** `IJobTrigger.TriggerAsync` ends in
`JobExecutor.DispatchAsync`, so a triggered run executes in the process that received the request —
there is no cross-process dispatch, because that would be a queue and §7 #1 and #4 of the design plan
cut queues on purpose. An API host over a different process's jobs answers every trigger with
`urn:cadence:problem:job-not-found`, which an earlier version of this sample did. Design plan §13.6
states the conclusion: *the supported shape is every replica mapping the API*.

### One worker, two AppHosts

The worker picks its storage tier from the connection string it was handed, and nothing else:

| Connection string | Tier | Started by |
|---|---|---|
| `cadence-redis` | `UseRedisStorage()` | `Cadence.Sample.AppHost.Redis` |
| `cadence-sql` | `UseSqlStorage()` | `Cadence.Sample.AppHost.Sql` |
| neither | in-memory stores, no-op coordinator | `dotnet run` on the worker alone |

It says which one it chose on the way up, and that line is how you know which sample you are looking
at:

```
Cadence.Sample.ClusteredWorker[4] Replica worker-vfhvbscc joining the cluster on the SQL Server storage tier.
Cadence.Sample.ClusteredWorker[4] Replica worker-npmdbedf joining the cluster on the Redis storage tier.
```

The jobs, the endpoint wiring, the demo timings and the OTel setup therefore exist once, so "the two
samples behave the same" is a fact about the code rather than a claim about two copies of it.

### Running either one

Docker must be running, and `./scripts/pack.ps1` first — the worker consumes `Cadence.Core`,
`Cadence.Api`, `Cadence.Storage.Sql` and `Cadence.Storage.Redis` from the local feed, so it is also
the check that all four pack correctly.

```powershell
./scripts/pack.ps1
dotnet run --project samples/Cadence.Sample.AppHost.Sql      # dashboard :17059, worker :5080, Keycloak :8080
dotnet run --project samples/Cadence.Sample.AppHost.Redis    # dashboard :17060, worker :5081, Keycloak :8081
```

Every port differs between the two so both can run at once, which is how the comparison below was
measured.

Each AppHost prints its dashboard URL with a login token; that dashboard is the UI here, and Cadence
tags every log record, span and metric with `JobName`, `RunId` and `InstanceId`, so "which replica ran
what" is answerable without `Cadence.Dashboard`, which is v0.4.

Three jobs run on every replica:

| Job | Cron | Triggers | What it is for |
|---|---|---|---|
| `tick-tock` | every 5s | `Schedule, Api` | one span per `job.scheduled_for` however many replicas are up |
| `slow-sweep` | every 10s, takes 25s | `Schedule` | overruns its own slot, on purpose; refuses an API trigger, so the 400 is reachable |
| `reindex-catalog` | none | `Api, Manual` | runs only when asked, so a triggered run cannot be mistaken for a scheduled one |

### The endpoint is proxied, and that is the point

Aspire puts one proxy in front of the three replicas, and the replicas never bind that port
themselves — the proxy does, which is what lets the AppHost fix it. It does, at `:5080` for the SQL
pair and `:5081` for the Redis one, because Keycloak matches a client's redirect URIs literally and
cannot be handed a port Aspire picked at startup. The spread across replicas behind that one port is
unchanged, and is still this sample's most useful demonstration.

```powershell
$b = 'http://localhost:5080'     # :5081 for the Redis AppHost
$t = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
```

`curl.exe` ships with Windows and prints non-2xx bodies, where `Invoke-RestMethod` throws — and a
401, a 404 and a 409 are half of what is worth seeing here. Every response below is from a real run;
where the two tiers answered differently the difference is called out, and where they did not, one
capture stands for both.

The token is `Cadence:Api:Tokens` in the worker's `appsettings.Development.json` — 64 hex characters
of `deadbeef`, the right *shape* and obviously not a secret. A real deployment generates one with
`openssl rand -hex 32`: a pause records its caller as `token:{first 8 hex of the token's SHA-256}`,
readable by anyone who can read `GET /pause`, so a guessable token can be confirmed offline without
touching the service.

### 1. The gate, and the registry

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "$b/cadence/api/jobs"
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/jobs"
```

```
401
```

```
name            cron           timeZone allowedTriggers nextOccurrenceUtc
----            ----           -------- --------------- -----------------
tick-tock       */5 * * * * *  UTC      Schedule, Api   2026-08-27 08:54:25
slow-sweep      */10 * * * * * UTC      Schedule        2026-08-27 08:54:30
reindex-catalog                UTC      Api, Manual
```

The tree is policied as a group, so every route on it answers 401 without a token. A *wrong* token
is also 401, not 403 — it never becomes an authenticated identity, so authorization is never
reached. There is no `WWW-Authenticate` header on the way out; nothing in Cadence promises one.

`reindex-catalog` has no cron, so its `cron` and `nextOccurrenceUtc` come back null: the shape a
trigger-only job has on the wire. Each response also carries a `lastRun` with the `instanceId` that
ran it — history is the cluster's, the registry is the process's.

### 2. Triggering, and who ends up running it

```powershell
1..6 | ForEach-Object { curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/tick-tock/trigger"; Start-Sleep -Milliseconds 500 }
```

```json
{"runId":"dbdd35fe-e262-4db4-809d-ed85792a2abc","jobName":"tick-tock","instanceId":"worker-npmdbedf"}
{"runId":"e161ef28-8ab1-452c-9bce-5ecd0bd946c5","jobName":"tick-tock","instanceId":"worker-dtmaxfrx"}
{"runId":"51ce76ed-34ae-4668-af0a-fb86d53dac37","jobName":"tick-tock","instanceId":"worker-uevvqfhq"}
```

202 and not 201: the run has been *accepted*, and there is no resource to point at yet. Three
different replicas, one unchanged request — and nothing chose them but the proxy, which assigns per
connection rather than per request. *Different replicas* is what holds; a rotation you can predict
is not.

Occasionally one answers `409 urn:cadence:problem:run-skipped` instead: that trigger landed on a
replica whose own 5-second occurrence was mid-flight, and `Skip` is evaluated **on this instance**,
per process, not across the cluster.

The contrast with scheduled runs is the whole thing in one table:

```powershell
$runs = (curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs?job=tick-tock&limit=500" | ConvertFrom-Json).runs
$runs | Group-Object trigger, instanceId | Select-Object Count, Name | Sort-Object Name
```

```
Count Name
----- ----
    3 Api, worker-cnzansdk
    3 Api, worker-jnhbmvbt
   43 Schedule, worker-vfhvbscc
```

Claiming elects one replica and keeps it. Triggering goes wherever the request landed.

### 3. One run per occurrence

The guarantee this sample has always existed to show, unchanged by the API being here. Count the
scheduled runs, then count the slots they claim:

```powershell
$scheduled = $runs | Where-Object trigger -eq 'Schedule'
"scheduled runs: {0}   distinct slots: {1}" -f $scheduled.Count,
  ($scheduled | Select-Object -ExpandProperty scheduledForUtc -Unique).Count
```

```
SQL     scheduled runs: 187   distinct slots: 187
Redis   scheduled runs: 130   distinct slots: 130
```

Equal on both tiers, and they stay equal — through a pause, through the store being stopped and
started under them, and through every trigger above. Three replicas all evaluate the same schedule
every second and all three try to claim; in SQL the unique index on `CadenceJobRun` picks one, in
Redis a key only one caller can create does.

**The winner does not rotate — and that is not load balancing.** All 43 slots in the table above went
to one replica. Whichever replica started first has its tick phase a few tens of milliseconds ahead,
and that is the whole race; the other two are failover capacity, not a share of the work. If you came
here expecting three replicas to divide the load three ways, this is the sample correcting you, and
§14.1 of the design plan is the architecture that would change it — recorded, deliberately not built.

Slots that go to a non-leader are the leader being *disturbed*: a pause it had already seen and the
others had not, or the store going away and coming back. Over the same twelve pause-and-resume cycles
the split came out differently on the two tiers:

```
SQL     183 worker-vfhvbscc    4 worker-cnzansdk
Redis    95 worker-npmdbedf   20 worker-uevvqfhq   15 worker-dtmaxfrx
```

A leader keeps its lead only while the others are a fraction of a second behind. On Redis all three
learn about a resume at once and genuinely race for the next slot; on SQL they learn at whatever phase
their own poll timer is at. Suggestive rather than proven — one window, one machine — and either way
the slot count never doubled.

**The `Skip` caveat, live.** `slow-sweep` takes longer than the gap between its occurrences. Within
one replica `Skip` is strict, so you will see runs recorded as `Skipped`. Across replicas it cannot
be, because the claim answers *"has anyone started this slot?"* and not *"is anyone running this
job?"* — so once the leader is busy, a different replica claims the next slot and starts while the
first is still going. Two overlapping spans, same `job.name`, different `job.instance_id`. A job
needing a hard cross-instance guarantee has to take its own lock.

**The janitor reaping a dead replica.** Kill the winning replica *hard* — `Stop-Process -Id <pid>
-Force` — while a `slow-sweep` is in flight. Not the dashboard's Stop button: that drains gracefully,
records the run as `Aborted` and deletes the registry row, leaving nothing for the janitor to find.
After a hard kill the heartbeat simply stops, and within `HeartbeatTimeout` plus one janitor pass the
run flips from `Running` to `Lost` — a distinct status meaning *nobody recorded an outcome at all*,
which is a different conversation with an operator than `Aborted`. Measured on SQL: killed at
08:23:35 mid-sweep, marked `Lost` at 08:23:56, and the surviving replica had taken over by the next
occurrence at 08:23:40.

### 4. Reading history, and the progress a run leaves behind

```powershell
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs/62150202-ad02-4f7a-8ebf-64f9b2705b86"
```

```json
{"run":{"runId":"62150202-ad02-4f7a-8ebf-64f9b2705b86","jobName":"tick-tock","status":"Succeeded","trigger":"Api","instanceId":"worker-uevvqfhq","startedAtUtc":"2026-08-27T06:56:12.0118188+00:00","completedAtUtc":"2026-08-27T06:56:12.2179599+00:00","duration":"00:00:00.2050000"},"log":[{"timestampUtc":"2026-08-27T06:56:12.0128964+00:00","message":"claimed and ran"}]}
```

That log entry is the job's `context.Report(...)` call, read back out of a store three processes
write to. The by-id read is the only place progress appears, because a list view renders none and
fetching it would be a second query per row.

`GET /runs` filters by `job`, `status`, `from`, `to`, `instance`, `limit` and `offset`, and returns
`limit` so a caller can see what clamping happened — ask for 10000 and you get 500. Note also what is
*absent* above: an API trigger is recorded as `Api` rather than `Manual`, which is reserved for a
dashboard button, and it carries no `scheduledForUtc` at all because it belongs to no occurrence.

### 5. Pause: one HTTP call, every replica

Pause is store-backed, so the request only has to reach *a* replica. Hold the schedule and leave
triggers open — the shape of an actual incident:

```powershell
curl.exe -s -i -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" `
  -d '{"scope":"Schedule","reason":"payment gateway incident"}' "$b/cadence/api/pause"
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/pause"
```

```
HTTP/1.1 204 No Content
{"scope":"Schedule","reason":"payment gateway incident","setBy":"token:247d08f3","setAtUtc":"2026-08-27T06:54:28.313+00:00"}
```

`setBy` is read from the authenticated principal, not from the request body — an audit trail cannot
be written by the caller being audited. `token:247d08f3` is the first 8 hex of this token's SHA-256,
stable across restarts.

Then watch the schedule stop moving. `tick-tock` is due every 5 seconds; on both tiers the last
occurrence landed within two seconds of the PUT and nothing followed it for the next 25:

```
PUT at 06:54:28.290Z, then every 5s: latest scheduled occurrence 06:54:30Z, unchanged
```

Had the pause reached only the replica that served the PUT, one of the other two would have started
winning the leader's abandoned slots. Silence on all three is the propagation — and *how fast* they
saw it is the one place these two samples measurably part company; see
[What actually differs](#what-actually-differs).

Triggers stay open the whole time, because the two switches are independent. Close the other switch
and the escape hatch shuts too:

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
`-d '{\"scope\":...}'` form PowerShell 5.1 needs sends literal backslashes instead.

### 6. The refusals, as problem documents

```powershell
curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/no-such-job/trigger"
curl.exe -s -X POST -H "Authorization: Bearer $t" "$b/cadence/api/jobs/slow-sweep/trigger"
curl.exe -s -X PUT -H "Authorization: Bearer $t" -H "Content-Type: application/json" -d '{"scope":"Everything"}' "$b/cadence/api/pause"
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/runs/00000000-0000-0000-0000-000000000000"
```

```json
{"type":"urn:cadence:problem:job-not-found","title":"Job not found","status":404,"detail":"No job is registered under the name 'no-such-job'."}
{"type":"urn:cadence:problem:trigger-not-allowed","title":"Trigger not allowed","status":400,"detail":"'slow-sweep' cannot be triggered by Api. 'slow-sweep' allows Schedule."}
{"type":"urn:cadence:problem:invalid-pause-scope","title":"Unknown pause scope","status":400,"detail":"'Everything' is not a pause scope. Use None, Schedule, Triggers or All."}
{"type":"urn:cadence:problem:run-not-found","title":"Run not found","status":404,"detail":"No run is recorded under the id '00000000-0000-0000-0000-000000000000'."}
```

404 for a name that does not exist, 400 for one that does but declares no `Api` trigger — no amount
of retrying turns the second into a 202, which is what makes it that shape of 4xx. Every one is
`application/problem+json` with a `urn:cadence:problem:` type, so a caller can branch on the type
rather than parsing prose. Byte-for-byte identical on both tiers, which is what you would hope: the
problem documents are Core's, not the store's.

The 404 is also how a misconfigured pod diagnoses itself. `MapCadenceApi()` does not throw on an
empty registry — registering jobs behind a feature flag is legitimate — it warns at map time, and
the trigger endpoint names the cause in the body.

One caveat: a *malformed* body is not Cadence's 400 but ASP.NET Core's, and in `Development` that
means the developer exception page, which echoes the request headers, your `Authorization` line
included. Not a reason to avoid it; a reason not to paste that output anywhere.

### 7. Probes, storage health and Swagger

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" "$b/health/live"                    # no token
curl.exe -s -o NUL -w "%{http_code}`n" "$b/cadence/api/health/storage"     # no token
curl.exe -s -H "Authorization: Bearer $t" "$b/cadence/api/health/storage"
```

```
200
401
```

```json
SQL     {"status":"Healthy","checks":[{"name":"cadence-sql","status":"Healthy","description":"The schedule database answered.","duration":"00:00:00.0029771"}]}
Redis   {"status":"Healthy","checks":[{"name":"cadence-redis","status":"Healthy","description":"Redis answered in 2 ms.","duration":"00:00:00.0041428"}]}
```

`/health/live` and `/health/ready` are anonymous because a kubelet cannot present a token.
`/cadence/api/health/storage` is inside the group and behind the gate, because it reports the last
store error — operator information, not a probe. It is also the fastest way to confirm which sample
you are talking to.

Now take the store away — `docker stop` its container, or stop the resource from the dashboard — and
ask again:

```
/health/live               200 Healthy
/health/ready              200
/cadence/api/health/storage
{"status":"Degraded","checks":[{"name":"cadence-sql","status":"Degraded","description":"The schedule
 database did not answer.","error":"Connection Timeout Expired. ...","duration":"00:00:11.7455250"}]}
```

**That is the split working.** The store is gone, the storage report says so and names the error, and
liveness stays 200 — because a storage blip that turned into a 503 on every replica at once would
have the orchestrator restart the entire cluster during exactly the incident it should be riding out.
Start the container again and the next check answers `Healthy` with no restart.

`Development` also gets an OpenAPI document at `/openapi/v1.json` and Swagger UI over it at
`/swagger`. **Authorize** takes the token above — paste the value alone, Swagger UI adds the `Bearer`
prefix:

```json
{"CadenceToken":{"type":"http","description":"A token from Cadence:Api:Tokens or CADENCE_API_TOKEN. Paste the token alone; Swagger UI adds the Bearer prefix.","scheme":"bearer"}}
```

That scheme comes from `OpenApiSecurity.cs` in this sample, not from the package: describing one means
referencing `Microsoft.AspNetCore.OpenApi`, and `Cadence.Api` will not put that dependency on hosts
that generate no document. The padlock is added per operation from the authorization metadata
`MapCadenceApi()` already stamped, so the gate's branches are read rather than re-decided.

### 8. The mapping gate refusing to start

The gate is a *startup* failure, on purpose: an operator meets it on deploy rather than a stranger
meeting the open endpoint. `--no-launch-profile` drops the `Development` the launch profile sets, and
`Production` does not load `appsettings.Development.json`, so no token is configured:

```powershell
dotnet run --project samples/Cadence.Sample.ClusteredWorker --no-launch-profile
```

```
Unhandled exception. Cadence.CadenceStartupException: MapCadenceApi() refuses to map outside
Development because nothing would authenticate it. Supply a token (CADENCE_API_TOKEN, or
Cadence:Api:Tokens), configure CadenceApiOptions.Oidc, or name an authorization policy with
CadenceApiOptions.RequireAuthorization, or — if something in front of this application already
authenticates callers — set CadenceApiOptions.AllowUnauthenticated.
   at Cadence.Api.CadenceApiEndpointExtensions.MapCadenceApi(IEndpointRouteBuilder endpoints)
```

The process exits without ever listening, and no store is involved: this run has no connection
string at all, so it is on the in-memory tier and the gate still stops it. Satisfy the gate with
`$env:CADENCE_API_TOKEN = '<64 hex characters>'` and the same command gets past the map, logging
which source supplied how many tokens and no value:

```
info: Cadence.Api[3002] Cadence's API accepted 1 token(s): 0 set in code, 0 from
      Cadence:Api:Tokens, 1 from CADENCE_API_TOKEN. Values are never logged.
```

That is the line to read when a token that ought to work does not.

### 9. Signing in through Keycloak, across three replicas

Everything above was a token. This is the other half of v0.3.1: a person, authenticated by a real
identity provider, holding a cookie that all three replicas can read.

Both AppHosts start `quay.io/keycloak/keycloak:26.7.2` alongside the store, in `start-dev` on the
image's embedded database, and import
[`samples/keycloak/cadence-realm.json`](keycloak/cadence-realm.json) on every boot. JSON has no
comments, so what is in it belongs here:

| In the realm | Value | Why |
|---|---|---|
| Realm | `cadence` | issuer `http://localhost:8080/realms/cadence`, or `:8081` on the Redis AppHost |
| Client | `cadence-dashboard`, confidential, standard flow only | Cadence is a server-side client and holds a secret; there is no implicit or password grant to enable |
| Secret | `cadence-dashboard-secret` | in the file on purpose, like the `deadbeef` token: the right shape, obviously not a secret |
| Redirect URIs | `http://localhost:5080/cadence/signin-oidc` and `:5081` | the callback is under `BasePath`, not at the framework's `/signin-oidc`, so a realm copied from a tutorial names the wrong path |
| Post-logout URIs | `http://localhost:5080/cadence/signout-callback-oidc` and `:5081` | the *provider's* return leg, not `SignedOutRedirectUri` — the browser reaches `/cadence` one hop later |
| Realm role | `cadence-operator` | mapped into the ID token as a flat `cadence_role` claim, because Keycloak's default `realm_access.roles` is nested JSON no claim comparison can read |
| User | `admin` / `admin123!`, holding that role | the claim is checked in `OnTokenValidated`, so a user without it is refused at the callback and never holds a session |
| That user's profile | `Cadence Admin`, `admin@cadence.invalid` | Keycloak's `VERIFY_PROFILE` action interrupts the first sign-in of a user with no name or email, so a stripped-down user meets an Update Account Information form instead of the callback |

The worker gets the authority, client id and secret as `CADENCE_OIDC_*` from the Keycloak resource,
and names the role itself:

```csharp
api.Oidc.RequiredClaimType = "cadence_role";
api.Oidc.RequiredClaimValue = "cadence-operator";
```

Leaving that pair null is legal and `MapCadenceApi()` warns about it, because it means every user
Keycloak authenticates may trigger jobs and pause the cluster. A sample should not demonstrate the
configuration the product warns about.

**Sign in.** Open `$b/cadence/api/auth/login` in a browser, and Keycloak asks for `admin` /
`admin123!`. You land back on `$b/cadence`, which is a 404 until `Cadence.Dashboard` in v0.4, holding
a `cadence.session` cookie. Copy its value out of devtools:

```powershell
$s = '<the cadence.session value>'
$h = @('-H', "Cookie: cadence.session=$s", '-H', 'X-Cadence-Session: 1')
curl.exe -s @h "$b/cadence/api/auth/me"
```

```json
{"kind":"user","name":"Cadence Admin","subject":"bb1ae66d-02fa-433e-9b17-9e3f8930c8c0","scope":"Operate"}
```

`kind` is `user` rather than `token`, and the scope is `Operate`: the surface has no finer grain for
a person. The subject is Keycloak's, and the ticket carries nothing else — no groups, no directory
attributes, no provider tokens. `X-Cadence-Session` is not optional: the same request without it
answers 401, because a cookie a cross-site form could send is a cookie Cadence will not honour.

**Now the part that needs three replicas.** Trigger with the cookie instead of the token:

```powershell
1..6 | ForEach-Object { curl.exe -s -X POST @h "$b/cadence/api/jobs/tick-tock/trigger"; Start-Sleep -Milliseconds 500 }
```

```json
{"runId":"42290df8-3118-4a25-838f-e6a879cb85e0","jobName":"tick-tock","instanceId":"worker-zmprvdnv"}
{"runId":"81166499-5d99-4c34-aedd-7405161e37b3","jobName":"tick-tock","instanceId":"worker-zmprvdnv"}
{"runId":"8ec207ab-74d1-4a06-a4a6-42b0d42cfd46","jobName":"tick-tock","instanceId":"worker-hgwbxnxw"}
{"runId":"ed1748bd-cefb-4822-beca-994c1f953f88","jobName":"tick-tock","instanceId":"worker-bgnrqmsr"}
{"runId":"d8e9858a-c1b6-42bd-bf83-aed9c81c7925","jobName":"tick-tock","instanceId":"worker-bgnrqmsr"}
{"runId":"2ccb81cf-a771-4c14-b6b1-1d4ed1b04e2a","jobName":"tick-tock","instanceId":"worker-bgnrqmsr"}
```

Three replica ids across six requests, one cookie, and nobody signed in three times. The spread is
the proxy's, assigned per connection rather than per request, exactly as in step 2.

The ticket is encrypted with a Data Protection key, and `ManageDataProtectionKeys` puts that key
ring in the store the schedules and run history are already in, under the application name
`Cadence` — so the replica that minted the ticket and the replica that reads it derive the same
key. Three containers or three pods is where that is load-bearing; on one developer machine the
host's own default key directory is shared anyway, which is why this is the property a deployment
has to test and a laptop cannot fail.

**Mint a token.** A signed-in user is how API tokens are issued, and the scope is where the coarse
grain of a cookie gets narrowed. Only a *recently* signed-in one: `TokenCreationMaxAge` is five
minutes, and a ticket older than that answers 401 with `WWW-Authenticate: CadenceCookie` rather than
403, because the fix is one redirect — to `/cadence/api/auth/login?prompt=login`, which the refusal
names and which makes Keycloak ask for the password again instead of handing back the same
`auth_time`.

```powershell
curl.exe -s @h -H "Content-Type: application/json" -X POST "$b/cadence/api/tokens" -d '{"name":"reader","scope":"Read"}'
```

```json
{"id":"b308a8d1-3370-47f2-9c37-536bc1d5b26b","name":"reader","fingerprint":"ba0b68d5","scope":"Read","createdAtUtc":"2026-08-28T06:11:26.6352369+00:00","token":"9fpNgd9HXytMMpZ1Gz8zp1SndanKXZdqOcfInvY6x5Y"}
```

The value appears exactly once. Read works, trigger does not:

```powershell
$r = '9fpNgd9HXytMMpZ1Gz8zp1SndanKXZdqOcfInvY6x5Y'
curl.exe -s -o NUL -w "%{http_code}`n" -H "Authorization: Bearer $r" "$b/cadence/api/jobs"
curl.exe -s -o NUL -w "%{http_code}`n" -H "Authorization: Bearer $r" -X POST "$b/cadence/api/jobs/tick-tock/trigger"
```

```
200
403
```

403 and not 401, unlike a wrong token: this one authenticated fine and then failed authorization,
which is the distinction the two status codes exist to make. Mint an `Operate` one, keeping its id
and value, and the same request is accepted:

```powershell
$t2 = curl.exe -s @h -H "Content-Type: application/json" -X POST "$b/cadence/api/tokens" -d '{"name":"deploy-pipeline","scope":"Operate"}' | ConvertFrom-Json
curl.exe -s -H "Authorization: Bearer $($t2.token)" -X POST "$b/cadence/api/jobs/tick-tock/trigger"
```

```json
{"runId":"737dfa1d-bf4a-4b28-9fd4-6b5d8fc350a8","jobName":"tick-tock","instanceId":"worker-hgwbxnxw"}
```

Then revoke it, with the cookie again, and try once more:

```powershell
curl.exe -s -o NUL -w "%{http_code}`n" @h -X DELETE "$b/cadence/api/tokens/$($t2.id)"
curl.exe -s -o NUL -w "%{http_code}`n" -H "Authorization: Bearer $($t2.token)" -X POST "$b/cadence/api/jobs/tick-tock/trigger"
```

```
204
401
```

**Immediately, and on every replica.** There is no cache to expire and no token lifetime to wait
out: the handler hashes the presented value and asks the store, so revocation is a row the next
request does not find. Back to 401 rather than 403, because a revoked token authenticates nobody.

**What this sample does that a real realm must not.** `sslRequired` is `none` and Keycloak serves
plain HTTP, so the worker sets `Oidc.RequireHttpsMetadata = false`, in `Development` only — a
deployment leaves it at its default. The client secret and the user's password are checked in.

That is the whole list. Signing out is *not* on it: Cadence names the client on the end-session
request for every provider, because the ticket carries no `id_token` to hint with and RP-Initiated
Logout permits `client_id` in its place. Keycloak refuses the request outright without one, which is
how that came to be Cadence's behaviour rather than this sample's.

## What actually differs

Everything above was driven against both samples with the same script, and every step came back
the same on both: same status codes, same problem documents, same `instanceId` spread, one run per
occurrence on both. That is the claim worth making, and the conformance suite is what keeps it true.

Four things did differ, and only the first is behaviour anyone would notice.

### A pause reaches the replicas ~9× faster on Redis

SQL polls a version counter every `SchedulePollInterval`; Redis publishes on a channel and polls only
as a backstop (§11.3 — pub/sub has no redelivery, so a scheduler that had silently stopped noticing
edits would look perfectly healthy). Pause rides the same counter (§12), so it inherits the
difference.

Measured the same way on both: hold the schedule, resume it, and time how long until the first
scheduled occurrence lands. `tick-tock` is due every 5 seconds and `SchedulePollInterval` is 5
seconds in both samples, so a replica that saw the resume at once catches the next boundary and one
that has to poll can miss it.

```
SQL                                        Redis
round 1:  14.0s later                      round 1:  1.2s later
round 2:   1.0s later                      round 2:  0.8s later
round 3:  10.9s later                      round 3:  0.8s later
round 4:  10.9s later                      round 4:  0.8s later
round 5:   1.0s later                      round 5:  0.8s later
round 6:  10.9s later                      round 6:  0.7s later

min 1.0s  max 14.0s  mean 8.1s             min 0.7s  max 1.2s  mean 0.9s
```

Redis caught the very next occurrence all six times. SQL caught it twice and missed it four times.
The absolute numbers are quantised by the 5-second cron, so read them as "next boundary" versus
"one or two boundaries later" rather than as latencies.

### Timestamps come back at different precision

`CadenceJobRun` stores `datetime2(3)`, so SQL rounds to the millisecond. Redis stores ticks and
returns them, so a run's timestamps come back at .NET's full precision. Same run, same job, two
tiers:

```
SQL     "startedAtUtc":"2026-08-27T06:54:27.643+00:00"
Redis   "startedAtUtc":"2026-08-27T06:56:12.0118188+00:00"
```

Nothing in the API promises a precision, and no filter depends on one, but a client that
round-trips the string and compares for equality would notice.

### The storage health check names its own tier

`cadence-sql` runs `SELECT 1` and answers *"The schedule database answered."*; `cadence-redis` runs
`PING` and answers *"Redis answered in 2 ms."* — the Redis check reports the round trip in its
description, the SQL one does not. Both are `Healthy` with a `duration`, and both go `Degraded` with
the driver's error text when the store is gone.

### Editing a schedule by hand is a different procedure

The dashboard that would do this properly is v0.4, so for now it is the store's own tooling — and on
both tiers there is the same trap: replicas do not poll the schedule table, they poll a single
version counter, which is what keeps "nothing changed" cheap. A hand-written edit has to bump it too,
or the edit sits there and nothing ever reads it.

`CadenceJobSchedule` holds **overrides**, not a seeded copy of what the code declared, so a job
running on its `[ScheduledJob]` cron has no row yet: you `INSERT`, you do not `UPDATE`.

```sql
BEGIN TRANSACTION;
INSERT INTO CadenceJobSchedule (JobName, CronExpression, TimeZoneId, Enabled, UpdatedAtUtc, UpdatedBy)
VALUES ('tick-tock', '*/20 * * * * *', 'UTC', 1, SYSUTCDATETIME(), 'me');
UPDATE CadenceScheduleVersion SET Version = Version + 1 WHERE Id = 1;
COMMIT TRANSACTION;
```

Redis holds the same override as one field of a hash, its per-job version in a parallel hash, and the
global counter as a plain key — and publishing is what makes it arrive in milliseconds rather than at
the next poll:

```
HSET   {cadence}:schedules tick-tock '{"CronExpression":"*/20 * * * * *","TimeZoneId":"UTC","Enabled":true}'
HINCRBY {cadence}:schedules:rowver tick-tock 1
INCR   {cadence}:schedules:version
PUBLISH {cadence}:schedules:changed <the value INCR returned>
```

`HDEL` both fields, then bump and publish again, to drop back to the declared cron. Verified against
the running sample: `GET /cadence/api/jobs` reported `*/20 * * * * *` on the first poll after the
`PUBLISH`, and `*/5 * * * * *` again after the `HDEL`.

Schedule writes are not on the API tree at all, and no sample can show them there: a token may start
work and stop it, while only a person changes *when* work happens.

### What did not differ, and one thing that cannot be seen from here

The claim is represented differently — a row in `CadenceJobRun` on SQL, a key
`{cadence}:occ:{job}:{ticks}` holding the winning run's id on Redis — but nothing observable depends
on which. Both are permanent rather than expiring (§11.1), both are removed by the janitor with the
run they belong to, and both produced exactly one run per occurrence over every window measured here.

The difference that would actually decide a deployment is invisible in a sample: with Redis's default
configuration a restart can lose recent writes, claims included, and an occurrence whose claim
vanished can be claimed again. The root [README](../README.md#choosing-a-storage-tier) has the table;
this sample cannot show it, because it would take killing Redis at the wrong microsecond.

## Per-sample specifics

Everything else about the two is shared. What is not:

### Cadence.Sample.AppHost.Sql

Starts `mcr.microsoft.com/mssql/server:2022-latest` as the resource `sql` and creates a database in
it exposed as the connection string `cadence-sql`. Aspire generates the SA password and injects the
whole connection string, so nothing is checked in and nothing is printable from the sample.
`SqlSchemaInitializer` creates the tables on first boot; three replicas booting together all try, the
first wins an application lock and the rest find nothing to do.

The worker will not start at all if the database is unreachable, which is why the AppHost `WaitFor`s
it.

### Cadence.Sample.AppHost.Redis

Starts `docker.io/library/redis` as the resource `cadence-redis`. Aspire 13.5 runs it with
`--requirepass` and TLS on 6379 (plaintext stays on 6380), and hands the worker a
StackExchange.Redis configuration string carrying both. **`UseRedisStorage()` takes it unchanged** —
no reshaping, no parsing, no options to set; that was the one thing worth checking before building
this sample.

There is no schema step and no migrator: a key exists once something writes it. Which also means the
worker starts with Redis unreachable, where the SQL worker cannot — `SqlSchemaInitializer` fails the
host, while the Redis tier logs each failed operation and keeps ticking. Confirmed by pointing the
worker at a closed port:

```
warn: Cadence.Storage.Redis.RedisScheduleSource[3102] Could not subscribe to schedule changes;
      falling back to polling every 00:00:05. Edits will be picked up, just not immediately.
```

That is the poll backstop earning its keep. `AbortOnConnectFail` is also forced on by the tier
whatever the connection string says, so an unreachable Redis throws rather than quietly queueing
commands: reporting "someone else won" because Redis was down is the silent skipped run the
coordinator contract forbids.

To look inside, from the container so the password is never on your command line:

```powershell
docker exec <container> sh -c 'redis-cli -p 6380 -a "$REDIS_PASSWORD" --no-auth-warning KEYS "{cadence}:*"'
```

The `{cadence}:` prefix is a hash tag, so every key Cadence writes lands in one slot and the Lua
scripts stay valid under Redis Cluster.

## Notes on both

**Timings are demo values, not defaults.** The worker sets `HeartbeatInterval` 5s,
`HeartbeatTimeout` 20s, `JanitorInterval` 15s and `SchedulePollInterval` 5s — the same numbers on both
tiers, so a difference between the samples can only come from the store. The real defaults are 15s /
60s / 5min / 10s, right for a deployment and wrong for standing in front of a screen: the janitor demo
would take five minutes. The relationship they encode holds either way — the timeout is four
heartbeats, so one missed beat never gets a live replica's runs reaped out from under it.

**Running with no infrastructure at all.** Hand the worker neither connection string and it falls
back to the in-memory stores and the no-op coordinator. No Docker, no store, and the whole control
surface still mounted on `http://localhost:5000`:

```powershell
dotnet run --project samples/Cadence.Sample.ClusteredWorker
```

```
info: Cadence.Sample.ClusteredWorker[4] Replica SEBASTIANS:44916:1d70a814 joining the cluster on
      the in-memory storage tier.
info: Cadence.Scheduling.CadenceHostedService[1004] Cadence started on instance
      SEBASTIANS:44916:1d70a814 with 3 job(s), ticking every 00:00:01.
```

What you lose is everything the pair is about: run history becomes a per-process ring that empties on
restart, every `instanceId` in every response is the same string, and there is no occurrence for
anyone else to lose the race for. `GET /cadence/api/health/storage` answers
`{"status":"Healthy","checks":[]}` — an empty list, because there is no store to check.

**What the samples still cannot show.** A history view and schedule editing in a UI: both are
`Cadence.Dashboard`, v0.4. The Aspire dashboard covers live telemetry per replica, but it reads OTel
rather than run history, so "what ran last Tuesday" is still a store query. Nor the loopback filter,
which engages on one branch of the gate — `Development`, no token, no policy, no
`AllowUnauthenticated` — and needs a host bound off loopback with the token blanked, which is not a
state these samples can be in. Design plan §13.3 documents the branch and the 403 it answers with.

**The guarantee is also proven without any of this.** `ClusteredSchedulingTests` runs five instances
against a Testcontainers store with a fake clock and asserts one run per occurrence, on every CI
build, deterministically, against both tiers. These samples exist for what a test cannot do: real
processes, real kills, real restarts, and something to look at.
