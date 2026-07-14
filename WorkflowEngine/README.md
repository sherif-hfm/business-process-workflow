# Workflow Engine

.NET 10 workflow runtime for definitions exported by `workflow-editor.html`.

## Projects

- `src/WorkflowEngine.Api` - ASP.NET Core Web API, endpoints and composition root.
- `src/WorkflowEngine.Service` - engine logic, service interfaces, repository ports.
- `src/WorkflowEngine.Infrastructure` - EF Core, PostgreSQL, migrations, repositories.
- `src/WorkflowEngine.Shared` - DTOs and C# workflow definition model.
- `src/WorkflowEngine.Ui` - Blazor Server UI that calls the API.
- `tests/WorkflowEngine.Tests` - xUnit definition/editor tests plus an in-process
  API host backed by an isolated PostgreSQL Testcontainer.
- `tools/*` - the existing definition verifier, live API regression runner, and
  instance load runner.

## Storage

- Workflow definitions are versioned JSONB snapshots in `workflow_definitions`.
- Runtime state is normalized in `workflow_instances`, `execution_tokens`,
  `user_tasks`, `multi_instance_executions`, `multi_instance_flow_counts`,
  `instance_variables`, and `instance_history`.
- Runtime mutations use one lock order: instance, multi-instance execution, then
  user tasks. Stale competing actions return 409 instead of advancing twice.
- Instance summary/detail projections include grouped active, pending, claimed,
  and assigned task counts. Claim ownership is exposed only by task DTOs; instance
  DTOs do not project a task claimant.
- Node and sequence-flow roles are enforced from the authenticated actor's JWT.

## Run Locally

Start PostgreSQL:

```powershell
docker compose up -d
```

Run the API:

```powershell
dotnet run --project .\src\WorkflowEngine.Api\WorkflowEngine.Api.csproj --launch-profile http
```

Run the Blazor UI:

```powershell
dotnet run --project .\src\WorkflowEngine.Ui\WorkflowEngine.Ui.csproj --launch-profile http
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
- `GET /api/instances/inbox` (actor-scoped)
- `GET /api/instances/{id}`
- `GET /api/instances/{id}/flows`
- `POST /api/instances/{id}/claim`
- `POST /api/instances/{id}/unclaim`
- `POST /api/instances/{id}/flows/{flowId}`
- `POST /api/instances/{id}/cancel`
- `GET /api/multi-instance-executions/{executionId}/flows`
- `POST /api/multi-instance-executions/{executionId}/flows/{flowId}`

The multi-instance execution endpoints expose only selectable interrupting flows
(`cancelRemainingInstances=true`) authorized by both the current node and flow
roles. They let an authorized actor interrupt the parent execution even without
an active child work item; unfinished child items are cancelled and the workflow
continues through the selected flow.

Known multi-instance `mode`, `source`, and `completionEvaluation` casing is
canonicalized when definitions are loaded. Unknown or explicitly null values,
duplicate node/flow ids, and case-variant duplicate variable names are rejected.
Cardinality and collection fan-out are bounded before allocation by
`Workflow.MultiInstance.MaxInstances`.

## Verification

Docker is required for the isolated PostgreSQL tests:

```powershell
dotnet test .\tests\WorkflowEngine.Tests\WorkflowEngine.Tests.csproj
dotnet run --project .\tools\MultiInstanceVerifier\MultiInstanceVerifier.csproj
dotnet run --project .\tools\MultiInstanceApiTests\MultiInstanceApiTests.csproj -- --manage-api
```

The live API runner writes Markdown and JSON evidence under the repository-level
`TestResults` directory and includes restart recovery plus a 1,000-item load case.

### Variable search

The list (`GET /api/instances`) and inbox (`GET /api/instances/inbox`) endpoints
accept repeated `var=name:value` query params to filter by instance variable
values, e.g. `GET /api/instances?var=reqno:4711&var=priority:high`. Each pair is
an exact, case-insensitive match on the variable's scalar value; multiple pairs
are AND-combined, and on the inbox the filter is additive on top of the caller's
role/claim scope. In the Blazor UI, the Instances and Inbox pages expose this as
a comma-separated `name:value` filter box.
