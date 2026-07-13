# Workflow Engine

.NET 10 workflow runtime for definitions exported by `workflow-editor.html`.

## Projects

- `src/WorkflowEngine.Api` - ASP.NET Core Web API, endpoints and composition root.
- `src/WorkflowEngine.Service` - engine logic, service interfaces, repository ports.
- `src/WorkflowEngine.Infrastructure` - EF Core, PostgreSQL, migrations, repositories.
- `src/WorkflowEngine.Shared` - DTOs and C# workflow definition model.
- `src/WorkflowEngine.Ui` - Blazor Server UI that calls the API.

## Storage

- Workflow definitions are versioned JSONB snapshots in `workflow_definitions`.
- Runtime state is normalized in `workflow_instances`, `instance_variables`, and `instance_history`.
- Runtime transitions lock the instance row with `SELECT ... FOR UPDATE`.
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

### Variable search

The list (`GET /api/instances`) and inbox (`GET /api/instances/inbox`) endpoints
accept repeated `var=name:value` query params to filter by instance variable
values, e.g. `GET /api/instances?var=reqno:4711&var=priority:high`. Each pair is
an exact, case-insensitive match on the variable's scalar value; multiple pairs
are AND-combined, and on the inbox the filter is additive on top of the caller's
role/claim scope. In the Blazor UI, the Instances and Inbox pages expose this as
a comma-separated `name:value` filter box.
