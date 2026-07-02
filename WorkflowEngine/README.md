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
- Roles are stored in the definition JSON but not enforced.

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
- `GET /api/instances/{id}`
- `POST /api/instances/{id}/claim`
- `POST /api/instances/{id}/unclaim`
- `POST /api/instances/{id}/actions/{actionId}`
- `POST /api/instances/{id}/cancel`
