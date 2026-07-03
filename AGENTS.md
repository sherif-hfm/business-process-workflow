# Business Process Workflow Editor

A single-file, dependency-free visual editor for designing business process
workflows in the browser. Users lay out **steps** (nodes) inside **phases**
(swimlane-style containers), connect steps with **actions** (directed edges),
attach typed **variables**, and save/load the whole model as JSON.

The model is a simplified, BPMN 2.0-inspired subset: steps are typed as
`startEvent`, `userTask`, `task`, or `endEvent`, drawn with BPMN-style shapes
(event circles, task rounded rectangles). See [BPMN alignment](#bpmn-alignment)
for how the editor's vocabulary maps to the BPMN standard and what is
intentionally simplified.

There is no build step, no server, and no external libraries. Everything runs
client-side using plain HTML, CSS, and vanilla JavaScript with inline SVG.

---

## Files

| File | Purpose |
| --- | --- |
| `workflow-editor.html` | The entire application: markup, CSS, and JS in one file. |
| `workflow.json` | A sample/exported workflow ("Purchase Request Approval") used as example data. It matches the JSON format the editor saves and loads. |
| `WorkflowEngine/` | .NET 10 Web API + Blazor Server workflow runtime using PostgreSQL. |
| `AGENTS.md` | This document. |

To run it, open `workflow-editor.html` directly in a modern browser. No install,
no server required.

---

## How it works (architecture)

Everything lives in `workflow-editor.html`. The key pieces:

- **State**: A single global `model` object holds the whole workflow. Interaction
  state lives in globals like `selected`, `connectMode`, `drag`, `phaseDrag`,
  and `phaseResize`.
- **Rendering**: `render()` is the top-level redraw. It calls `renderPhases()`,
  `renderEdges()`, `renderNodes()`, `renderInspector()`, and `renderHint()`.
  The canvas is an `<svg>` with three layer groups: `#phases`, `#edges`, `#nodes`
  (drawn in that back-to-front order).
- **Inspector**: The right-hand `<aside id="inspector">` is a context panel that
  edits whatever is selected (step, action, or phase). Built dynamically via
  `field()`, `selectField()`, `variableRow()`, and `variableCheckbox()` helpers.
- **Interaction**: Pointer events on the SVG drive dragging steps, dragging
  phases (which moves their contained steps), and resizing phases.
  "Connect mode" lets the user click a source step then a target step to create
  an action edge between them. For pass-through steps (a `startEvent` or an
  automatic `task`), connect mode sets the source's `nextStepId` instead of
  creating an action.
- **Persistence**: `save()` serializes `model` to pretty-printed JSON (uses the
  File System Access API `showSaveFilePicker` when available, otherwise falls
  back to a download). Loading reads a JSON file and normalizes it through
  `loadFromObject()`, which fills in sensible defaults for missing fields.
- **Seed data**: `seedSample()` populates the "Purchase Request Approval" example
  on load.

There are no automated tests. Validation is manual, in-browser.

---

## Runtime engine (`WorkflowEngine/`)

`WorkflowEngine/` is a separate .NET 10 solution for running workflow instances
from the JSON definitions produced by the editor. It preserves the editor's JSON
format rather than normalizing the definition into step/action tables.

Projects:

- `src/WorkflowEngine.Api` - API layer: Minimal API endpoints, OpenAPI, startup
  composition, development migration/seed.
- `src/WorkflowEngine.Service` - Service layer: workflow engine behavior,
  definition validation, service interfaces, repository ports, and DI extension.
- `src/WorkflowEngine.Infrastructure` - Infrastructure layer: EF Core,
  PostgreSQL/Npgsql, JSONB mapping, migrations, repository implementations, and
  unit of work.
- `src/WorkflowEngine.Shared` - shared DTOs and C# model for the editor JSON.
- `src/WorkflowEngine.Ui` - Blazor Server UI that calls the API through a typed
  `HttpClient`.

Storage follows the hybrid design:

- Workflow definitions are immutable/versioned JSONB snapshots in
  `workflow_definitions`.
- Runtime state is normalized in `workflow_instances`, `instance_variables`, and
  `instance_history`.
- Instance transitions run in a database transaction and lock the instance row
  with `SELECT ... FOR UPDATE`; there is no in-memory run engine state.
- Automatic `task` steps immediately follow `nextStepId` and write an
  `automatic` history row. A hop limit (`steps.Count + 1`) guards against
  cycles. Legacy JSON with `autoAdvance: true` is migrated to `type: "task"` on
  load.
- `requiresClaim` **is** enforced at runtime: such a step must be claimed
  (`POST /claim`) before its actions are available or can be taken, only the
  claiming user may act, and the claim is released on transition. `unclaim`
  clears it. Claim ownership is tracked on `workflow_instances.claimed_by`
  (users default to `anonymous` when unspecified).
- Required `variables` are validated when starting an instance (chosen start
  event variables) and when taking an action (action variables); missing required
  values are rejected.
- Instances move through `Running`, `Completed` (on entering an `endEvent`
  step), and `Cancelled` (`POST /cancel`) statuses.
- Roles are retained in the definition JSON but are **not** enforced by the
  current API/UI.

Definitions are versioned: `POST /api/workflows` creates v1, `PUT
/api/workflows/{id}` creates a new immutable version, and only a *published*
definition can start instances.

### HTTP API

- `WorkflowDefinitionEndpoints` (`/api/workflows`): `GET /` (latest per
  definition), `GET /{id}`, `POST /` (create), `PUT /{id}` (new version),
  `POST /{id}/publish`, `DELETE /{id}`.
- `WorkflowInstanceEndpoints` (`/api/instances`): `POST /` (start),
  `GET /?status=`, `GET /{id}`, `GET /{id}/actions` (available actions),
  `POST /{id}/claim`, `POST /{id}/unclaim`,
  `POST /{id}/actions/{actionId}` (take action), `POST /{id}/cancel`.
- `WorkflowDomainException` maps to problem responses for invalid operations
  (unpublished workflow, missing variable, bad claim, unavailable action, etc.).

### Blazor UI pages

- `/workflows` (`Workflows.razor`) - list definitions.
- `/workflows/{id}/start` (`StartInstance.razor`) - fill start variables and
  launch an instance.
- `/instances` (`Instances.razor`) - list instances, filterable by status.
- `/instances/{id}` (`InstanceDetail.razor`) - claim/unclaim, take actions,
  view variables and history.

The UI talks to the API through `WorkflowApiClient` (a typed `HttpClient`).

To run locally from `WorkflowEngine/`:

```powershell
docker compose up -d
dotnet run --project .\src\WorkflowEngine.Api\WorkflowEngine.Api.csproj --launch-profile http
dotnet run --project .\src\WorkflowEngine.Ui\WorkflowEngine.Ui.csproj --launch-profile http
```

The API listens on `http://localhost:5017` and the UI on
`http://localhost:5152` by default. In development, the API applies EF
migrations and seeds the root `workflow.json` as the first published definition
when the database is empty.

---

## Data model

The workflow is a plain JSON object. See `workflow.json` for a real example.

```jsonc
{
  "id": 1,
  "name": "Purchase Request Approval",
  "initialStepId": 1,          // id of the start event step (nullable)
  "phases": [ /* Phase[] */ ],
  "steps":  [ /* Step[]  */ ]
}
```

### Phase
A visual container (swimlane) with position and size. Steps whose center falls
inside a phase are assigned to it (`step.phaseId`).

```jsonc
{
  "id": 1,
  "name": "Submission",
  "x": 29, "y": 95,            // top-left position on canvas
  "w": 1082, "h": 172          // width / height (min 220 x 150)
}
```

### Step
A node in the workflow. `type` is one of `startEvent`, `userTask`, `task`, or
`endEvent` (BPMN-inspired, simplified).

```jsonc
{
  "id": 1,
  "name": "Draft",
  "type": "startEvent",        // "startEvent" | "userTask" | "task" | "endEvent"
  "phaseId": 1,                // owning phase id, or null
  "x": 69, "y": 155,           // top-left position on canvas
  "roles": [ "Requester" ],    // free-text candidate roles (userTask only)
  "requiresClaim": false,      // if true, one user must claim before acting (userTask only)
  "nextStepId": 2,             // target for startEvent and automatic task (nullable)
  "variables": [ /* Variable[] */ ], // start event: data required to start the workflow
  "actions":   [ /* Action[]   */ ]  // outgoing edges (userTask only); none on startEvent/endEvent/task
}
```

Step kinds:

- **`startEvent`** (was `start`): workflow entry; circle on canvas. Has start
  `variables` and a single `nextStepId` (no actions). Auto-advances into
  `nextStepId` when an instance is started.
- **`userTask`** (was `task`): human step; rounded rectangle with user-task
  marker. Has `roles`, `actions`, optional `requiresClaim`.
- **`task`**: automatic pass-through (was `autoAdvance: true`); rounded
  rectangle with AUTO marker. Follows `nextStepId` on entry with no user action.
- **`endEvent`** (was `end`): terminal; thick-ring circle. No outgoing actions.

A workflow may define multiple `startEvent` steps. `initialStepId` is the
default start event. `POST /api/instances` accepts optional `startStepId` to
force a different start event; that event's variables are collected.

Legacy JSON using `start` / `task` / `end` and `autoAdvance` is migrated on
load in both the editor and the .NET engine (legacy start actions become
`nextStepId` plus merged variables).

### Action
A directed transition from one step to another (rendered as an edge/arrow).
Action ids are conventionally `stepId * 100 + n` (e.g. step 2 -> 201, 202).

```jsonc
{
  "id": 101,
  "name": "Submit",
  "toStepId": 2,               // target step id
  "roles": [ "Requester" ],    // free-text roles allowed to take this action
  "variables": [ /* Variable[] */ ] // data captured when the action is taken
}
```

### Variable
Typed data attached to a start event or an action.

```jsonc
{
  "id": 1,
  "name": "reqno",
  "dataType": "number",        // "string" | "number" | "boolean" | "date" | "datetime"
  "isArray": false,
  "required": true
}
```

---

## BPMN alignment

The model is a deliberately small subset of BPMN 2.0. Names and shapes follow
the standard where practical; runtime semantics are simplified. Use this mapping
when extending the model so new features stay close to BPMN terminology.

### Concept mapping

| This project | BPMN 2.0 concept | Notes / simplifications |
| --- | --- | --- |
| `step` (node) | Flow node | Umbrella term for events and tasks. |
| `type: "startEvent"` | None Start Event | Single entry marker per flow; drawn as a thin-ring circle. Carries start `variables` (BPMN would model these as data inputs / form fields). |
| `type: "userTask"` | User Task | Human-performed activity; rounded rectangle with a user marker. |
| `type: "task"` | Abstract/automatic Task | Pass-through activity completed with no user action; closest to a BPMN Task/Service Task without an implementation. |
| `type: "endEvent"` | None End Event | Terminal marker; thick-ring circle. |
| `action` (edge) | Sequence Flow + user decision | A named outgoing transition a user selects. BPMN would split this into a sequence flow plus a gateway when there are multiple choices. |
| `phase` | Lane (within a Pool) | Swimlane-style container; assignment is geometric, not a formal participant/pool model. |
| `roles` | Lane / Performer (Potential Owner) | Free-text candidate roles; not a formal resource/assignment model. |
| `variables` | Data Object / Property | Typed data captured at the start event or on an action. |
| `requiresClaim` | User Task "claim" (assignee) | Runtime-enforced single-owner locking; not a distinct BPMN element. |
| `nextStepId` (auto/start) | Unconditional Sequence Flow | Direct, no-decision transition used by `startEvent` and automatic `task`. |

### Intentional deviations from BPMN

- **No gateways.** Branching is expressed as multiple `actions` on a `userTask`.
  There is no exclusive/parallel/inclusive gateway element yet.
- **No message/timer/signal events.** Only plain (none) start and end events
  exist.
- **No pools / collaboration.** Phases are lanes only; there is no multi-party
  pool or message flow.
- **Sequence flow is embedded.** Edges live inside a step's `actions` (or its
  `nextStepId`) rather than as first-class flow objects with their own ids.
- **Roles are advisory** in the definition and only `requiresClaim` ownership is
  enforced at runtime.

### If you add BPMN-aligned features later

- Prefer BPMN names for new step types/elements (e.g. `serviceTask`,
  `exclusiveGateway`, `timerStartEvent`) and add matching shapes.
- Keep the JSON tolerant: extend `loadFromObject()` (editor) and
  `WorkflowModelMigrator` (.NET) so older documents still load.
- Update `WorkflowStepTypes` predicates (`IsStart`, `IsEnd`, `IsAutomatic`,
  `IsUserTask`) and `ValidateDefinition` together so the engine and editor agree.

---

## Key conventions & invariants

- **Node/phase sizing**: user tasks and automatic tasks use `NODE_W` x `NODE_H`
  (170 x 64) rounded rectangles. Start/end events use `EVENT_D` (56) circles with
  the name label below. Phases have minimums `MIN_PHASE_W` x `MIN_PHASE_H`
  (220 x 150).
- **ID generation**: `nextStepId`, `nextPhaseId`, `nextActionId`, and
  `nextVariableId` derive new ids from existing max ids. Action ids are namespaced
  per step (`step.id * 100 + n`).
- **Start event**: `model.initialStepId` marks the default start event (must be a
  `startEvent`). Multiple start events are allowed. Deleting the default
  reassigns it to the first remaining start event (or null).
- **Referential cleanup**: deleting a step also removes any actions pointing to it
  and nulls out any `nextStepId` that referenced it;
  deleting a phase nulls out `phaseId` on its steps.
- **Phase assignment is geometric**: on drag end, a step's `phaseId` is set based
  on which phase its center lands in (`assignStepPhase`).
- **Load is tolerant**: `loadFromObject()` accepts alternate field names
  (`label` for `name`, `initialStep`, `next` for `toStepId`) and backfills
  missing coordinates so older/partial JSON still renders.
- **Roles**: steps and actions carry a free-text `roles` string array (candidate
  roles). `loadFromObject()` normalizes it via `normalizeRoles()`, migrating a
  legacy singular `role` string into an array and defaulting to `[]`. Roles are
  edited with the `rolesField()` chip editor and shown as small labels on nodes
  and edges.
- **Requires claim**: `userTask` steps carry a boolean `requiresClaim`
  (design-time intent; enforced at runtime in `WorkflowEngine/`). Hidden for
  other step types.
- **Automatic task**: `type: "task"` follows `nextStepId` on entry with no user
  action. A dashed "auto" edge is drawn on the canvas. Legacy `autoAdvance:
  true` migrates to `type: "task"`.
- **Start event flow**: `type: "startEvent"` uses `nextStepId` (solid "start"
  edge on canvas). No actions. On instance start the engine collects that event's
  variables then auto-advances via `nextStepId`.

---

## Common tasks for an AI editing this project

- **Add a new field to a step/action/variable**: update the relevant
  `render*Inspector` function (UI), the `loadFromObject` normalizer (persistence),
  and `seedSample` if the example should show it. `save()` needs no change since
  it serializes `model` directly.
- **Add a new step type**: extend `STEP_TYPE_OPTIONS` in `renderStepInspector`,
  add matching CSS under `.node.<type>`, and update `WorkflowStepTypes` in the
  .NET shared model.
- **Add a new variable data type**: append to the `VARIABLE_DATA_TYPES` array.
- **Change canvas visuals**: edit the `<style>` block (CSS variables live in
  `:root`; node/phase/edge styling is grouped by class).

Keep editor changes in the single HTML file unless there is a strong reason to
split it. Preserve the editor's no-dependency, no-build nature. Runtime engine
changes belong under `WorkflowEngine/`.
