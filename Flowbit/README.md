# Flowbit

.NET 10 workflow runtime for definitions exported by `flowbit-editor.html`.

## Projects

- `src/Flowbit.Api` - ASP.NET Core Web API, endpoints and composition root.
- `src/Flowbit.Service` - engine logic, service interfaces, repository ports.
- `src/Flowbit.Infrastructure` - EF Core, PostgreSQL, migrations, repositories.
- `src/Flowbit.Shared` - DTOs and C# workflow definition model.
- `src/Flowbit.Ui` - Blazor Server UI that calls the API.
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
  `flowbit.multi_instance_executions`, `flowbit.multi_instance_flow_counts`,
  `flowbit.instance_variables`, `flowbit.instance_history`,
  `flowbit.sequence_flow_occurrences`, and `flowbit.sequence_flow_summaries`.
- Runtime mutations use one lock order: instance, multi-instance execution, then
  user tasks. Stale competing actions return 409 instead of advancing twice.
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

Open:

- API OpenAPI JSON: `http://localhost:5017/openapi/v1.json`
- Blazor UI: `http://localhost:5152`

In development, the API applies migrations and seeds the root `workflow.json` as a published workflow if the database is empty.

## Main API

- `GET /api/workflows`
- `GET /api/workflows/{id}`
- `POST /api/workflows`
- `PUT /api/workflows/{id}`
- `POST /api/workflows/{id}/publish`
- `POST /api/instances`
- `GET /api/instances?status=running`
- `GET /api/instances?includeVariables=true`
- `GET /api/instances/inbox` (actor-scoped)
- `GET /api/instances/inbox?includeVariables=true` (actor-scoped)
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
inheritance. Every real change is recorded in instance history. Workflows without
`taskAssignmentRoles` expose no manageable tasks.

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

### Variable search

The list (`GET /api/instances`) and inbox (`GET /api/instances/inbox`) endpoints
accept repeated `var=name:value` query params to filter by instance variable
values, e.g. `GET /api/instances?var=reqno:4711&var=priority:high`. Each pair is
an exact, case-insensitive match on the variable's latest scalar value; multiple
pairs are AND-combined, and on the inbox the filter is additive on top of the
caller's role/claim scope. In the Blazor UI, the Instances and Inbox pages expose
this as a comma-separated `name:value` filter box.

The instance list and inbox also accept `includeVariables=true`. Each returned
item then includes a `variables` object containing the latest JSON value for
every variable name; instances without variables receive an empty object. The
property is omitted when the parameter is absent or false. The instance list
loads variables only when requested; the inbox already projects them for its
stored-state capability checks, so including them adds no database query.

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
tie-breaker keeps offset pages deterministic. Invalid, duplicate, or excessive
sort clauses return 400.

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
