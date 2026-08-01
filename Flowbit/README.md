# Flowbit

.NET 10 workflow runtime for definitions exported by `flowbit-editor.html`.

## Projects

- `src/Flowbit.Api` - ASP.NET Core Web API, endpoints and composition root.
- `src/Flowbit.Service` - engine logic, service interfaces, repository ports.
- `src/Flowbit.Infrastructure` - EF Core, PostgreSQL, migrations, repositories.
- `src/Flowbit.Shared` - DTOs and C# workflow definition model.
- `src/Flowbit.Ui` - Blazor Server UI that calls the API.
- `src/Flowbit.Worker` - durable PostgreSQL job/timer dispatcher, lease
  heartbeats, timer-start reconciliation, metrics, and retention cleanup.
- `tests/Flowbit.Tests` - xUnit definition/editor tests plus an in-process
  API host backed by an isolated PostgreSQL Testcontainer.
- `tools/*` - the existing definition verifier, live API regression runner, and
  instance load runner.

## Storage

- All Flowbit tables, owned sequences, indexes, constraints, and EF migration
  history live in the fixed PostgreSQL `flowbit` schema.
- Workflow definitions are versioned JSONB snapshots in
  `flowbit.workflow_definitions`.
- Runtime state is normalized in `flowbit.workflow_instances`,
  `flowbit.execution_tokens`, `flowbit.user_tasks`,
  `flowbit.gateway_executions`, `flowbit.gateway_branches`,
  `flowbit.complex_gateway_states`,
  `flowbit.multi_instance_executions`, `flowbit.multi_instance_flow_counts`,
  `flowbit.node_executions`, `flowbit.instance_variables`,
  `flowbit.instance_variable_current_values`,
  `flowbit.instance_history`,
  `flowbit.sequence_flow_occurrences`, and `flowbit.sequence_flow_summaries`.
- Durable work is stored in `flowbit.workflow_jobs`,
  `flowbit.workflow_job_attempts`, `flowbit.workflow_job_snapshots`,
  `flowbit.workflow_incidents`, and `flowbit.timer_subscriptions`.
- Runtime mutations use one lock order: instance, active gateway
  executions/states/branches, active tokens, active multi-instance executions,
  then active/pending user tasks; rows are ordered by ID within each group.
  Stale competing actions return 409 instead of advancing twice.
- Instance summary/detail projections include grouped active, pending, claimed,
  and assigned task counts. Claim ownership is exposed only by task DTOs; instance
  DTOs do not project a task claimant.
- Node and sequence-flow roles are enforced from the authenticated actor's JWT.
- Workflow-level `taskAssignmentRoles` authorize assignment managers to assign,
  reassign, and unassign active normal or multi-instance user-task work items.
- Optional workflow-level `taskDistribution` credentials authorize an external
  distributor across every active version of the stable workflow key. Credential
  values may be literal or `${setting.*}` / `${config.*}` references; prefer
  references because literal secrets are visible in versioned definition JSON.
- A normal user task with `requiresAssignment=true` is stored as active but is
  omitted from regular inbox/task reads until it has an assignee. Assignment
  managers and the external distributor can still see it. `assignmentMode`
  supports `fresh`, `previous`, and `fromNode` ownership inheritance; this gate
  is intentionally separate from `requiresClaim` and cannot be combined with it.
- `flowbit.instance_variables` remains the append-only variable audit history.
  An `AFTER INSERT` trigger transactionally upserts the newest row per
  `(InstanceId, VariableName)` into `flowbit.instance_variable_current_values`.
  Search and latest-value enrichment read that bounded projection; execution
  detail and audit reads continue to use history.

## Gateways and scoped interruption

Gateway direction is inferred from topology. A split has exactly one incoming
and at least two outgoing flows; a merge has at least two incoming and exactly
one outgoing flow. One-in/one-out and many-in/many-out gateways are rejected, so
split and merge behavior must be authored as separate nodes. Their types are
independent: for example, a Parallel split may feed an Inclusive or Exclusive
merge.

- Exclusive splits keep ordered first-match/default routing; Exclusive merges
  pass each arriving token without synchronization.
- Parallel splits select every outgoing flow and Parallel merges wait for every
  static incoming flow.
- Inclusive splits select every true condition (or the required default).
  Inclusive merges use cached graph reachability plus current active token
  positions to apply the unpaired BPMN enabling rule and retain surplus tokens
  for later batches.
- Complex gateways persist a per-instance/node phase and cycle. Their
  `activationCondition` can use literal `IncomingCount(flowId)`,
  `TotalIncomingCount()`, and outgoing conditions can use
  `[gateway.waitingForStart]` to distinguish start from reset routing.

Every Parallel, Inclusive, or Complex firing is recorded in
`gateway_executions`; diverging firings create generic branch lineage even when
only one flow is selected. `scopedInterruptEvent` is a Flowbit extension whose
`gatewayRef` targets a structurally reachable Parallel, Inclusive, or Complex
split. It interrupts the nearest active matching activation in the triggering
token's ancestry, cancels sibling and nested work atomically, and follows its
single authored continuation. A stale event records `scopedInterruptSkipped`
and continues without cancellation.

The fan-out limit is configured by `Workflow.Gateway.MaxActiveTokens` and
defaults to 1000. Adjacency and reachability plans are cached per immutable
workflow-definition ID.

## Instance-wide flow evidence (`FlowInfo`)

Definitions can query fixed-size, instance-lifetime summaries for a sequence
flow. NCalc uses a literal flow id and property path:

```text
FlowInfo(201, 'actions.count')
Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')
FlowInfo(201, 'traversals.last.kind')
FlowInfo(201, 'all')
```

Supported paths are `all`; `actions.count`; `actions.last.user`, `userRoles`,
`occurredAt`, `kind`, or `values`; and the equivalent `traversals.*` paths.
`all` returns this shape (an unused known flow has zero counts and null `last`
values):

```json
{
  "flowId": 201,
  "actions": { "count": 1, "last": { "user": "alice", "userRoles": ["Manager"], "occurredAt": "...", "kind": "userTaskAction", "values": {} } },
  "traversals": { "count": 1, "last": { "user": "alice", "userRoles": ["Manager"], "occurredAt": "...", "kind": "userTaskAction", "values": {} } }
}
```

JavaScript script tasks use the same read-only shape through
`execution.getFlowInfo(201)`, for example
`execution.getFlowInfo(201).actions.last.userRoles`.

`actions` count explicit actor selections; `traversals` count token movement.
A normal user-task selection and a parent multi-instance interrupt are both; a
multi-instance child vote is action-only; its aggregate winner/default is
traversal-only; and automatic routing is traversal-only. The last evidence
snapshots the validated actor and all their roles at action time, so a directly
following gateway can route on the role of the user who selected the flow.

`FlowInfo` is allowed only in exclusive-gateway conditions, multi-instance
completion conditions, NCalc script assignments, and JavaScript script tasks.
It is not available in user-action visibility conditions, assignee/cardinality
expressions, or variable/output/header validation. `CountFlow` and `PercentFlow`
are unchanged and remain scoped to the current multi-instance execution.

Each recorded event appends to the audit ledger and transactionally updates one
summary row per instance/flow. Runtime expressions load summaries once and do
not scan or load detailed history, so evaluation cost does not grow with loops
or multi-instance fan-out. Definitions that do not reference `FlowInfo` skip the
summary query and write no occurrence or summary rows. The additive migration
does not backfill or guess old events; existing instances expose only evidence
recorded after deployment.

## Durable async work and timers

`task`, `serviceTask`, `scriptTask`, and `userTask` support `asyncBefore` and
`asyncAfter`. A multi-instance user task applies those flags to its parent
execution, not to every child item. Definitions may also use
`timerStartEvent`, `intermediateTimerCatchEvent`, and `timerBoundaryEvent` with
exactly one fixed ISO-8601 `timeDate`, `timeDuration`, or `timeCycle` schedule.
Timer boundaries may be interrupting or noninterrupting and may recur.

The worker leases bounded work with PostgreSQL `FOR UPDATE SKIP LOCKED`.
Workflow mutations still use the instance-first runtime lock order and verify
the job lease generation, token activation, wait phase, and subscription before
committing. Service calls and JavaScript execution happen between short staging
and finalization transactions, so the external/CPU-heavy body holds neither an
instance lock nor a database transaction. Output targets use variable-version
conflict detection; a concurrent write opens an incident instead of silently
overwriting it.

Worker defaults are eight total slots, six activity slots, and four concurrent
activity jobs per instance. Two slots therefore remain available for
timer/control work. PostgreSQL notifications are wake-up hints; one-second
polling remains authoritative, and an idle dispatcher shortens its backoff to
the next persisted due or lease-expiry deadline. Calls have at-least-once
external semantics and receive stable `sys.jobId` and `sys.jobAttempt` values,
so downstream services should use `sys.jobId` as an idempotency key.

Durable automatic activity lineages are bounded by the shared engine setting
`Workflow.Async.MaxConsecutiveAutomaticActivations` (default `1000`; missing,
invalid, or nonpositive values use that default). The 1,001st consecutive
automatic `task`, `serviceTask`, or `scriptTask` activation is persisted as an
`automatic_loop_limit` incident before its body can run. User-task actions,
multi-instance completion, message delivery, and timer triggers start a fresh
allowance. An administrator can retry the incident to queue the same stable job
as activation one of a new allowance.

The Blazor **Operations** page and `/api/jobs` / `/api/incidents` resources
default to the `admin` role. Override the comma-separated global role list with
the `WorkflowJobs.RequiredRole` engine setting. Job, incident, and attempt
collections use opaque keyset cursors; their list rows intentionally exclude
snapshots, result payloads, stack traces, and embedded attempt collections.
Instance summaries expose only grouped open/queued/running/incident counts and
the nearest due time.

For an additive rollout, first deploy the schema and API with
`WorkflowDurableProcessing:PublicationEnabled=false`. Draft definitions may
still be saved, but publishing or making an async/timer definition the default
is rejected. Start at least one worker and confirm `/health/ready` returns 200
after its first successful durable-queue query, then set the gate to `true`.
Existing definitions and running instances remain synchronous and require no
backfill.

Standard .NET meters publish worker and engine signals under `Flowbit.Worker`
and `Flowbit.Runtime.Jobs`. They include queue depth/age, timer lateness,
acquisition and instance-lock wait latency, lease loss, retries, output
conflicts, incidents, and cleanup volume. The worker also exposes these signals
in Prometheus text format at `/metrics`; `/health/live` is its process liveness
probe. These operational endpoints listen on `FlowbitWorker:HealthListenUrl`
(`http://0.0.0.0:8081` by default). Timer-start reconciliation uses one
transaction-scoped advisory leader and processes at most
`FlowbitWorker:TimerStartReconcileBatchSize` workflow families per pass, so
additional replicas provide failover without duplicating the full scan.

## Run Locally

Start PostgreSQL:

```powershell
docker compose up -d
```

Databases created before the `flowbit` schema was introduced must be recreated
once during development. The application intentionally does not relocate an
existing `public."__EFMigrationsHistory"` table automatically.

Run the API:

```powershell
dotnet run --project .\src\Flowbit.Api\Flowbit.Api.csproj --launch-profile http
```

Run the Blazor UI:

```powershell
dotnet run --project .\src\Flowbit.Ui\Flowbit.Ui.csproj --launch-profile http
```

Run one or more worker replicas:

```powershell
dotnet run --project .\src\Flowbit.Worker\Flowbit.Worker.csproj
```

Open:

- API OpenAPI JSON: `http://localhost:5017/openapi/v1.json`
- Blazor UI: `http://localhost:5152`

In development, the API applies migrations and seeds the root `workflow.json` as a published workflow if the database is empty.

### Gateway migration rollout

The generic gateway runtime is intentionally incompatible with the retired
parallel-specific schema and JSON vocabulary. For a deployment that predates
`inclusiveGateway`, `complexGateway`, and `scopedInterruptEvent`:

1. Stop every Flowbit API and worker process.
2. Back up anything that must be retained, then reset the `flowbit` schema (or
   recreate the database).
3. Apply the current EF Core migrations to the empty schema.
4. Deploy and start the matching API and UI versions.
5. Re-import workflow definitions using only the canonical gateway node types
   and `scopedInterruptEvent.gatewayRef`.

Do not run old and new binaries against the same database. Legacy
`parallelInterruptEvent`, `parallelGatewayRef`, parallel-specific runtime rows,
and in-flight gateway executions are not migrated.

## Main API

- `GET /api/workflows`
- `GET /api/workflows/{id}`
- `POST /api/workflows`
- `PUT /api/workflows/{id}`
- `POST /api/workflows/{id}/publish`
- `POST /api/instances`
- `GET /api/instances?status=running` (SQL-authorized, opaque cursor paging)
- `GET /api/instances?includeVariables=true`
- `POST /api/instances/search` (advanced SQL-backed variable search)
- `GET /api/instances/inbox` (actor-scoped)
- `GET /api/instances/inbox?includeVariables=true` (actor-scoped)
- `POST /api/instances/inbox/search` (advanced, actor-scoped)
- `POST /api/user-tasks/manage/search` (advanced, assignment-manager scoped)
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/search`
- `GET /api/node-executions` (authorized cross-workflow activity)
- `POST /api/node-executions/search` (advanced authorized activity search)
- `GET /api/node-executions/{id}` (authorized execution detail)
- `GET /api/jobs` (admin by default; opaque cursor paging)
- `GET /api/jobs/statistics`
- `GET /api/jobs/{id}`
- `GET /api/jobs/{id}/attempts` (opaque cursor paging)
- `GET /api/incidents` (admin by default; opaque cursor paging)
- `GET /api/incidents/{id}`
- `POST /api/incidents/{id}/retry`
- `GET /api/instances/{id}`
- `GET /api/instances/{id}/flows`
- `POST /api/instances/{id}/claim`
- `POST /api/instances/{id}/unclaim`
- `POST /api/instances/{id}/flows/{flowId}`
- `POST /api/instances/{id}/cancel`
- `GET /api/user-tasks/manage` (assignment-manager scoped)
- `POST /api/user-tasks/{taskId}/assign`
- `POST /api/user-tasks/{taskId}/unassign`
- `GET /api/task-distribution/workflows/{workflowKey}/tasks`
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/assign`
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/unassign`
- `GET /api/auth/context` (server-resolved workflow actor and roles)
- `GET /api/multi-instance-executions/{executionId}/flows`
- `POST /api/multi-instance-executions/{executionId}/flows/{flowId}`

## Node execution activity

`GET /api/node-executions` and `POST /api/node-executions/search` are the
read-only, cross-workflow activity search resources. The Blazor **Activity**
page uses the POST search route. `GET /api/node-executions/{id}` returns one
authorized execution with its execution-local detail. These routes do not grant
assignment, claim, cancellation, or workflow-mutation authority; the existing
inbox and Task Assignments APIs remain the action surfaces for human work.

A node execution represents one token visit to one node. Gateway splits
therefore produce a visit for each spawned branch token. A normal user task
shares one execution with its work item. A multi-instance user task instead
produces one execution per child work item and deliberately has no duplicate
parent execution row. Execution kind is `node` for a token visit and
`userTaskItem` for a multi-instance child. The supported lifecycle statuses are
`pending`, `active`, `completed`, `cancelled`, `faulted`, and `merged`;
completion reasons distinguish
normal/user/message/multi-instance work, gateway firing/interruption behavior,
caught errors, scoped or instance cancellation, and terminal end behavior. The
reason values are `normal`, `userAction`, `messageDelivery`,
`multiInstanceItem`, `multiInstanceCompleted`, `multiInstanceInterrupt`,
`boundaryCaught`, `normalEnd`, `terminateEnd`, `errorEnd`,
`instanceCancelled`, `gatewayScopeCancelled`, `gatewayJoinMerged`,
`parallelFork`, `parallelJoin`, `inclusiveSplit`, `inclusiveMerge`,
`complexActivation`, `complexReset`, `scopedInterrupt`, and
`scopedInterruptSkipped`.

The list returns `PagedResult<NodeExecutionSummaryDto>` and the detail route
returns `NodeExecutionDetailDto`. Summary rows include immutable workflow,
instance, token, node, branch, flow, actor, ownership, lifecycle, duration, and
cutover correlation. Detail adds actor and node-role snapshots, user-task
assignment and claim data, multi-instance item/result context, committed failure
information (descriptions are bounded to 1,000 Unicode characters), and only
variable changes attributed to that execution. It does not
present current instance variables as if they were an execution-time snapshot.
For those writes, `sourceActionId` retains the runtime source and can therefore
identify either a selected sequence flow or an automatic/message/boundary node.

List filters cover execution, instance, workflow version/key/version, business
key, token, user-task, multi-instance, branch, item, node, lifecycle, actor,
owner, flow, and timestamp/duration fields. Inclusive `From` and exclusive `To`
bounds are used for created, started, updated, and completed time ranges.
Repeated status, node-type, instance-status, and completion-reason values are
OR-combined within their group; different groups are AND-combined.

| Filter group | Query parameters |
| --- | --- |
| Identity/context | `executionId`, `instanceId`, `workflowId`, `workflowKey`, `workflowVersion`, `businessKey`, `tokenId`, `userTaskId`, `multiInstanceExecutionId`, `gatewayBranchId`, `itemIndex` |
| Node/lifecycle | `executionKind`, `nodeId`, `nodeName`, `nodeExternalId`, repeated `nodeType`, repeated `status`, repeated `instanceStatus`, repeated `completionReason`, `isMultiInstance`, `isCutoverSeeded` |
| People/flows | `owner`, `startedBy`, `completedBy`, `enteredViaFlowId`, `selectedFlowId`, `exitedViaFlowId`, `aggregateFlowId` |
| Time/duration | `createdFrom`, `createdTo`, `startedFrom`, `startedTo`, `updatedFrom`, `updatedTo`, `completedFrom`, `completedTo`, `minDurationMilliseconds`, `maxDurationMilliseconds` |

Repeated `var=name:value` parameters keep the existing instance-search
semantics: each pair tests the owning instance's **latest scalar value** using an
exact case-insensitive comparison. Up to ten pairs may be supplied and all must
match. They are current-instance filters, not historical values captured at
execution time.

Up to three unique `sort=field:asc|desc` clauses may select `id`, `instanceId`,
`workflowId`, `nodeId`, `createdAt`, `startedAt`, `updatedAt`, `completedAt`, or
`duration`. The default is `updatedAt:desc,id:desc`; nullable fields use
`NULLS LAST`, and an ID tie-breaker is always applied. Paging defaults to page 1
and 50 rows, permits at most 200 rows, and reports an exact database-backed
`totalCount`. Invalid identifiers, enums, time ranges, durations, variables, or
sort clauses return 400.

Visibility is evaluated in SQL before count, ordering, and paging. A caller can
read a row when one of their JWT roles appears either in the dynamic global
reader setting or in that immutable workflow version's `taskAssignmentRoles`.
Set the global reader roles in `flowbit.engine_settings` with namespace
`NodeExecution`, key `RequiredRole`, and a comma-separated value. A missing or
blank value defaults to `admin`. Both routes require authentication, so an
unauthenticated caller receives 401. An authenticated caller with no visible
workflow versions gets an empty page; an out-of-scope detail ID returns 404. UI
code intentionally does not duplicate or hard-code these role rules.

Node execution tracking is complete only for committed work from the migration
cutover onward. The migration seeds open active/pending user-task work plus
active non-multi-instance token visits, marks those rows with
`isCutoverSeeded=true`, and uses the cutover time for seeded active
`startedAt` values. It does not fabricate completed executions from legacy
`instance_history`. A failed transition that rolls back has no committed
execution record; a caught service/script failure is committed as a faulted host
execution followed by its boundary execution. Execution rows are retained
indefinitely with their owning workflow instance.

`instance_history` remains a heterogeneous transition and audit log used by
legacy instance detail and claim/assignment inheritance. It is not a complete
node-lifecycle ledger and should not be used to derive visit cardinality,
durations, or every cancellation.

The multi-instance execution endpoints expose only selectable interrupting flows
(`cancelRemainingInstances=true`) authorized by both the current node and flow
roles. They let an authorized actor interrupt the parent execution even without
an active child work item; unfinished child items are cancelled and the workflow
continues through the selected flow.

Task assignment mutations use `expectedUpdatedAt` for optimistic concurrency and
accept an optional audit reason. Assignment clears any existing claim and creates
direct ownership; unassignment clears both ownership fields and restores the
node's authored `requiresClaim` setting. For `requiresAssignment` tasks, unassign
returns the work item to the hidden external-assignment queue without rerunning
inheritance. Every real assignment change is recorded as an instance-history
audit entry. Workflows without `taskAssignmentRoles` expose no manageable
tasks.

The task-distribution endpoints are machine-facing and do not use JWT roles.
They authenticate `X-Client-Id` / `X-Client-Secret` against `taskDistribution`
on the current published default definition, while listing and mutating tasks
across all versions of that workflow family. They preserve the same optimistic
concurrency and audit behavior as manager actions. The list is minimal by
default; `includeVariables=true` adds latest instance variables for the returned
page. Missing configuration disables external distribution. Production callers
must use TLS and should be rate-limited at the gateway. The distributor list is
also the authoritative queue for unassigned `requiresAssignment` tasks; regular
users cannot discover, claim, or act on those tasks. Instance detail responses
redact `taskDistribution.clientSecret`.

Example configuration:

```json
"taskDistribution": {
  "clientId": "workforce-service",
  "clientSecret": "${setting.taskDistribution.clientSecret}"
}
```

Known multi-instance `mode`, `source`, and `completionEvaluation` casing is
canonicalized when definitions are loaded. Unknown or explicitly null values,
duplicate node/flow ids, and case-variant duplicate variable names are rejected.
Cardinality and collection fan-out are bounded before allocation by
`Workflow.MultiInstance.MaxInstances`.

The JWT claim used as the canonical workflow actor can be configured in
`flowbit.engine_settings` with namespace `Authentication`, key
`UserIdentityClaim`, and a stable claim name such as `sub` or `oid`. The value is
loaded once at API startup. If the row is absent, the API retains the legacy
`Identity.Name`/`NameIdentifier` selection; a configured claim that is missing or
invalid causes a 401. Restart all API replicas after changing it, and migrate or
drain active assignments and claims before switching identity formats.

## Verification

Docker is required for the isolated PostgreSQL tests:

```powershell
dotnet test .\tests\Flowbit.Tests\Flowbit.Tests.csproj
dotnet run --project .\tools\MultiInstanceVerifier\MultiInstanceVerifier.csproj
dotnet run --project .\tools\MultiInstanceApiTests\MultiInstanceApiTests.csproj -- --manage-api
```

The live API runner writes Markdown and JSON evidence under the repository-level
`TestResults` directory and includes restart recovery plus a 1,000-item load case.

## Advanced variable search

Five additive POST endpoints accept endpoint-specific native selectors plus a
shared Mongo-inspired `variableFilter` object:

| Endpoint | Scope retained from its GET counterpart |
| --- | --- |
| `POST /api/instances/search` | Instance selectors, structured sort, cursor/paging, and `includeVariables` |
| `POST /api/instances/inbox/search` | Actor inbox selectors, structured sort, paging, and `includeVariables` |
| `POST /api/user-tasks/manage/search` | Task, instance, owner/ownership selectors and manager-scoped paging |
| `POST /api/task-distribution/workflows/{workflowKey}/tasks/search` | Distribution selectors, paging, and `includeVariables`; workflow key stays in the route and credentials stay in headers |
| `POST /api/node-executions/search` | All execution selectors/ranges, structured sort, and paging |

An empty JSON object preserves the corresponding unfiltered GET defaults. POST
sorting uses objects such as `{ "field": "updatedAt", "direction": "desc" }`;
each endpoint retains its existing allowed fields, default order, cursor rules,
page limits, authorization, status codes, and `PagedResult<T>` response shape.

Instance search example:

```http
POST /api/instances/search
Content-Type: application/json

{
  "workflowKey": "health-certificate",
  "variableFilter": {
    "$and": [
      { "request.medicalCenter.id": { "$eq": "MC-1042" } },
      { "request.services": { "$contains": "health-certificate" } }
    ]
  },
  "sort": [{ "field": "updatedAt", "direction": "desc" }],
  "pageSize": 50,
  "includeVariables": true
}
```

Actor inbox example:

```http
POST /api/instances/inbox/search
Content-Type: application/json

{
  "variableFilter": {
    "request.medicalCenter.id": { "$eq": "MC-1042" }
  },
  "sort": [{ "field": "taskUpdatedAt", "direction": "desc" }],
  "page": 1,
  "pageSize": 50,
  "includeVariables": true
}
```

Assignment-manager example:

```http
POST /api/user-tasks/manage/search
Content-Type: application/json

{
  "ownership": "unassigned",
  "variableFilter": { "request.region": { "$in": ["north", "central"] } },
  "page": 1,
  "pageSize": 50
}
```

External distributor example (credentials remain transport headers, not body
fields):

```http
POST /api/task-distribution/workflows/health-certificate/tasks/search
X-Client-Id: workforce-service
X-Client-Secret: <secret>
Content-Type: application/json

{
  "owner": "clinic-queue",
  "variableFilter": {
    "request.services": { "$containsAny": ["health-certificate", "screening"] }
  },
  "includeVariables": true,
  "page": 1,
  "pageSize": 50
}
```

Node-execution example:

```http
POST /api/node-executions/search
Content-Type: application/json

{
  "statuses": ["completed"],
  "createdFrom": "2026-07-01T00:00:00Z",
  "variableFilter": { "request.medicalCenter.id": { "$eq": "MC-1042" } },
  "sort": [{ "field": "updatedAt", "direction": "desc" }],
  "page": 1,
  "pageSize": 50
}
```

The first segment of a dotted field identifies the Flowbit variable and later
segments address JSON object properties. Array indexes are not supported; use
containment operators or `$elemMatch`. If a variable name or JSON key itself
contains a dot, use the unambiguous `$field` form:

```json
{
  "$field": {
    "$var": "request.medicalCenter",
    "$path": ["id"],
    "$eq": "MC-1042"
  }
}
```

Inside `$elemMatch`, the same escape hatch is element-relative: omit `$var`
and provide `$path` for a dotted property name on the current array element.

```json
{
  "items": {
    "$elemMatch": {
      "$field": {
        "$path": ["medical.center", "id"],
        "$eq": "MC-1042"
      }
    }
  }
}
```

Supported logical operators are `$and`, `$or`, and `$not`. Supported comparison
operators are `$eq`, `$eqIgnoreCase`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`,
`$lt`, `$lte`, `$exists`, `$contains`, `$containsAny`, `$containsAll`, and
`$elemMatch`. Multiple ordinary fields are an implicit AND, as are multiple
operators on one field. A logical node cannot mix logical and field members;
wrap them in an explicit `$and` instead. `$elemMatch` evaluates its complete
nested predicate against one array element.

`$eq` is typed and case-sensitive; use `$eqIgnoreCase` for strings. Object
equality follows normalized PostgreSQL `jsonb` equality, while array equality is
order-sensitive. `$in` and `$nin` accept scalar alternatives; use containment
operators for JSON arrays/objects. Range operators accept JSON numbers only in
this version. Dates and datetimes support equality/membership but not ranges.
Missing paths do not satisfy comparisons, including `$ne` and `$nin`;
`$exists:false` is the only missing-path match, and explicit JSON `null` remains
distinct from a missing path.

Requests reject unknown operators, mixed node shapes, raw JSONPath, `$where`,
regex, executable expressions, and type-invalid operands with 400. A search body
is limited to 64 KiB; `variableFilter` is limited to five logical levels, 20
comparison predicates, 100 values per membership operator, and 16 JSON path
segments.

The validated filter is compiled only from whitelisted, parameterized PostgreSQL
templates and runs before count, sorting, and paging. Every repository composes
its mandatory role, actor, assignment, claim, delegation, workflow-family, or
execution-visibility predicate with the variable filter using AND. Consequently,
a caller's `$or` or `$not` cannot weaken authorization. Variable filtering is a
search predicate, not authorization: derive tenant/medical-center constraints
from trusted identity data, and keep outgoing-flow conditions as action-time
guards. No result membership is filtered in memory.

`flowbit.instance_variables` remains the append-only audit source. Migration
backfill plus an `AFTER INSERT` trigger maintains
`flowbit.instance_variable_current_values`, keeping only the greatest source ID
per instance/name. Its variable-name, `jsonb_ops` GIN, root case-insensitive
string, and root numeric indexes support the shared SQL compiler; targeted
expression indexes can be added later for measured hot nested paths. Latest
`includeVariables` enrichment also reads this projection.

All legacy GET routes remain unchanged. Repeated `var=name:value` filters retain
their exact case-insensitive latest-scalar-text behavior and are translated to
the shared SQL filter internally; repeated values remain AND-combined (including
the node-execution route's existing ten-filter limit). The Blazor Instances,
My Work, Task Assignments, Activity, and Task Distribution search surfaces use
the POST routes on every load, including an empty advanced filter; dashboard
summary reads retain their lightweight GET calls. `includeVariables=true` still
returns the latest JSON value for each name, omitting the property when false or
absent.

All five search surfaces expose a developer/admin multiline JSON editor with
syntax/root-object validation and formatting. API validation remains
authoritative for operators and operand types. Instances, My Work, and Task
Distribution can request and expand returned variable values; Task Assignments
and Activity use variables only as search predicates because their POST
contracts do not return them. The read-only `/task-distribution` diagnostic UI
is shown only in the Development environment; direct navigation outside
Development redirects to the UI home page. It remains
credential-authenticated by workflow key and `X-Client-Id` /
`X-Client-Secret`; credentials remain component state, the secret is sent only
as a header, and neither credential is placed in a URL or persisted by the UI.
These controls are diagnostics, not authorization: production clients must
derive tenant or medical-center predicates from trusted identity data.

### Instance and inbox sorting

The instance list and inbox accept up to three repeated `sort=field:direction`
parameters in priority order. Directions are `asc` and `desc`.

```text
GET /api/instances?sort=createdAt:asc&sort=id:desc
GET /api/instances/inbox?sort=instanceCreatedAt:desc&sort=userTaskId:asc
```

Instance fields are `id`, `createdAt`, and `updatedAt`. Inbox fields are
`userTaskId`, `instanceId`, `taskCreatedAt`, `taskUpdatedAt`,
`instanceCreatedAt`, and `instanceUpdatedAt`. Without an explicit sort, the
existing defaults remain `updatedAt DESC, id DESC` and
`taskUpdatedAt DESC, userTaskId DESC`, respectively. An implicit unique-ID
tie-breaker keeps pages deterministic. The instance list returns an opaque
`nextCursor`; pass it back as `cursor` for the next page. A cursor is bound to
its sort order, and a later page without the preceding cursor is rejected.
Instance visibility is applied in SQL before the exact count, ordering, paging,
and grouped job enrichment: callers with a role in
`WorkflowInstances.RequiredRole` (default `admin`) see every version, while
other authenticated callers see versions whose `taskAssignmentRoles` intersect
their JWT roles. Invalid, duplicate, or excessive sort clauses and malformed
cursors return 400.

Inbox responses expose explicit task and instance creation/update timestamps.
The older `createdAt` and `updatedAt` properties remain compatibility aliases
for `taskCreatedAt` and `taskUpdatedAt`.

## User tasks

Each token resting on a `userTask` owns a persisted work item. Task-addressed routes are authoritative when an instance may have more than one active work item:

| Route | Purpose |
| --- | --- |
| `GET /api/user-tasks/{taskId}` | Read an authorized task, its completion metadata, and caller capabilities. |
| `GET /api/user-tasks/{taskId}/flows` | List actions currently visible and executable by the caller. |
| `POST /api/user-tasks/{taskId}/claim` | Claim an active shared-pool task. |
| `POST /api/user-tasks/{taskId}/unclaim` | Release a claim as its owner or through a workflow `unclaimRoles` override. |
| `POST /api/user-tasks/{taskId}/flows/{flowId}` | Execute a visible action with optional typed variables. |
| `POST /api/user-tasks/{taskId}/assign` | Assign or reassign a task as a configured task-assignment manager. |
| `POST /api/user-tasks/{taskId}/unassign` | Return a managed assignment to its authored pool behavior. |
| `GET /api/user-tasks/manage` | Page through active tasks authorized by `taskAssignmentRoles`. |
| `GET /api/instances/{instanceId}/user-tasks` | Page through the caller's authorized task history for one instance. |
| `GET /api/instances/inbox` | List active work and caller-specific claim/action capabilities. |

The older instance-addressed `/api/instances/{id}/claim`, `/unclaim`, `/flows`, and `/flows/{flowId}` routes delegate to the same task core. They return `409` when no single active task can be addressed safely.

### Conditions are visibility and execution guards

A condition on a sequence flow leaving a user task is evaluated against values already stored on the workflow instance. For example, `amount > 5000` makes the action visible only while the stored `amount` satisfies that expression. The action endpoint re-evaluates the same condition while holding the instance/task lock, before applying submitted action variables. A caller cannot reveal or execute a hidden flow by posting a new `amount` in that same action.

Invalid or unresolved expressions evaluate to `false`. Task, flow, assignment, claim, and role checks still apply independently.

Inbox membership, ordering, and `TotalCount` come entirely from the database page. Latest variables and multi-instance state are loaded in page-bounded batches, after which flow roles, bypass roles, and conditions refine only the returned task's visible actions and capabilities. If no action is available, the task remains in the page with `canAct=false` and `canClaim=false`; service evaluation never removes or reorders an inbox item.

### Acting without a claim

An action may set:

```json
{
  "canActWithoutClaim": true,
  "canActWithoutClaimRoles": ["Supervisor"]
}
```

The bypass roles are additional to the user task's roles and the sequence flow's normal `roles`. They are checked only when the caller does not own the claim. Consequently, a normal claimant may take the action without a bypass role, while a supervisor may take it unclaimed or despite another actor's claim. An empty or missing `canActWithoutClaimRoles` list preserves the historical behavior: every otherwise-authorized actor may bypass. Claim bypass never overrides direct assignment.

### Capabilities and completion metadata

`UserTaskDto.capabilities` contains `claimedByMe`, `canClaim`, `canUnclaim`, and `canAct` for the current caller. The server derives these flags from active state, assignment, node and flow roles, stored conditions, claim ownership, bypass roles, and multi-instance ownership rules. Clients should render controls from these flags and still handle a locked re-check failure caused by concurrent activity.

Completed tasks expose `selectedFlowId`, `completedBy`, `result`, and `completedAt`. Normal task action history is correlated with both `tokenId` and `userTaskId`. Cancelled tasks do not receive action outcome metadata.
