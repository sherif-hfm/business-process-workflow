# Business Process Workflow Editor

A single-file, dependency-free visual editor for designing business process
workflows in the browser. Users lay out **flow nodes** inside **lanes**
(swimlane-style containers), connect them with **sequence flows** (directed
edges), attach typed **variables**, and save/load the whole model as JSON.

The model is a simplified, BPMN 2.0-aligned subset. Flow nodes are typed as
`startEvent`, `userTask`, `task`, `exclusiveGateway`, or `endEvent`, drawn with
BPMN-style shapes (event circles, task rounded rectangles, gateway diamonds).
Connections are first-class `sequenceFlows` with their own ids. See
[BPMN alignment](#bpmn-alignment) for how the vocabulary maps to the standard
and what is intentionally simplified.

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
  state lives in globals like `selected`, `connectMode`, `drag`, `laneDrag`,
  and `laneResize`.
- **Rendering**: `render()` is the top-level redraw. It calls `renderLanes()`,
  `renderEdges()`, `renderNodes()`, `renderInspector()`, and `renderHint()`.
  The canvas is an `<svg>` with three layer groups: `#lanes`, `#edges`, `#nodes`
  (drawn in that back-to-front order).
- **Inspector**: The right-hand `<aside id="inspector">` is a context panel that
  edits whatever is selected (a flow node, a sequence flow, or a lane). Built
  dynamically via `field()`, `selectField()`, `variableRow()`, and
  `variableCheckbox()` helpers.
- **Interaction**: Pointer events on the SVG drive dragging nodes, dragging lanes
  (which moves their contained nodes), and resizing lanes. "Connect mode" lets
  the user click a source node then a target node to create a `sequenceFlow`
  between them.
- **Persistence**: `save()` serializes `model` to pretty-printed JSON (uses the
  File System Access API `showSaveFilePicker` when available, otherwise falls
  back to a download). Loading reads a JSON file and normalizes it through
  `loadFromObject()`, which detects the schema and migrates legacy documents.
- **Seed data**: `seedSample()` builds the "Purchase Request Approval" example
  (including an exclusive gateway).

There are no automated tests. Validation is manual, in-browser.

---

## Runtime engine (`WorkflowEngine/`)

`WorkflowEngine/` is a separate .NET 10 solution for running workflow instances
from the JSON definitions produced by the editor. It preserves the editor's JSON
format rather than normalizing the definition into node/flow tables.

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
  `instance_history`. These tables keep integer ids and their original column
  names (`CurrentStepId`, `ActionId`, `FromStepId`, `ToStepId`,
  `SourceActionId`); the columns now simply carry flow-node / sequence-flow ids,
  so no database migration was needed for the BPMN rename.
- Instance transitions run in a database transaction and lock the instance row
  with `SELECT ... FOR UPDATE`; there is no in-memory run engine state.
- **Pass-through routing** (`ResolvePassThroughAsync`): `startEvent`, automatic
  `task`, and `exclusiveGateway` nodes are resolved in the same transaction until
  the instance rests on a `userTask` or terminates on an `endEvent`. A hop limit
  (`flowNodes.Count + 1`) guards against cycles. History rows are written with a
  `start`, `automatic`, or `gateway` note.
- **Exclusive gateways** evaluate outgoing flows: the first flow whose
  `condition` is true wins; otherwise the `isDefault` flow is taken; if neither
  matches the transition fails. Conditions use a minimal language
  (`SequenceFlowConditionEvaluator`): `variable op literal` with
  `== != < <= > >=`, or a bare variable name (truthy check).
- `requiresClaim` **is** enforced at runtime: such a `userTask` must be claimed
  (`POST /claim`) before its flows are available or can be taken, only the
  claiming user may act, and the claim is released on transition. `unclaim`
  clears it. Claim ownership is tracked on `workflow_instances.claimed_by`
  (users default to `anonymous` when unspecified).
- Required `variables` are validated when starting an instance (chosen start
  event variables) and when taking a sequence flow (flow variables); missing
  required values are rejected.
- Instances move through `Running`, `Completed` (on entering an `endEvent`), and
  `Cancelled` (`POST /cancel`) statuses.
- **Node roles are enforced** at runtime for `userTask` nodes. The caller's
  identity and roles come from a validated JWT (name + role claims), not from
  request fields. A `userTask` with a non-empty `roles` list can only be
  claimed/acted on by a caller holding one of those roles; an empty `roles` list
  is open to anyone. `GetAvailableFlowsAsync` hides flows when the role does not
  match; `ClaimAsync`/`TakeFlowAsync` reject with a `WorkflowDomainException`.
  Sequence-flow `roles` are still advisory (not enforced).
- Authentication: the API validates a bearer JWT (`Microsoft.AspNetCore.Authentication.JwtBearer`)
  using a shared symmetric key (`Jwt:Key`, dev only) and requires it on the
  `/api/instances` group. The Blazor UI mints its own token from the `/token`
  page (`DevTokenFactory` + `TokenState`) so a tester can switch user/roles on
  the fly; `AuthTokenHandler` attaches it as a `Bearer` header. For production,
  swap `AddJwtBearer` to a real OIDC identity provider and remove the UI minting
  page.

Definitions are versioned: `POST /api/workflows` creates v1, `PUT
/api/workflows/{id}` creates a new immutable version, and only a *published*
definition can start instances.

### HTTP API

- `WorkflowDefinitionEndpoints` (`/api/workflows`): `GET /` (latest per
  definition), `GET /{id}`, `POST /` (create), `PUT /{id}` (new version),
  `POST /{id}/publish`, `DELETE /{id}`.
- `WorkflowInstanceEndpoints` (`/api/instances`): `POST /` (start; optional
  `startEventId`), `GET /?status=`, `GET /{id}`, `GET /{id}/flows` (available
  sequence flows), `POST /{id}/claim`, `POST /{id}/unclaim`,
  `POST /{id}/flows/{flowId}` (take a flow), `POST /{id}/cancel`.
- `WorkflowDomainException` maps to problem responses for invalid operations
  (unpublished workflow, missing variable, bad claim, unavailable flow,
  gateway with no matching/default flow, etc.).

### Blazor UI pages

- `/workflows` (`Workflows.razor`) - list definitions.
- `/workflows/{id}/start` (`StartInstance.razor`) - pick a start event, fill its
  variables, and launch an instance.
- `/instances` (`Instances.razor`) - list instances, filterable by status.
- `/instances/{id}` (`InstanceDetail.razor`) - claim/unclaim, take available
  sequence flows, view variables and history.

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
  "initialEventId": 1,           // id of the default start event (nullable)
  "lanes": [ /* Lane[] */ ],
  "flowNodes": [ /* FlowNode[] */ ],
  "sequenceFlows": [ /* SequenceFlow[] */ ]
}
```

### Lane
A visual container (swimlane) with position and size. Flow nodes whose center
falls inside a lane are assigned to it (`flowNode.laneId`).

```jsonc
{
  "id": 1,
  "name": "Submission",
  "x": 29, "y": 95,            // top-left position on canvas
  "w": 1082, "h": 172          // width / height (min 220 x 150)
}
```

### FlowNode
A node in the workflow. `type` is one of `startEvent`, `userTask`, `task`,
`exclusiveGateway`, or `endEvent`.

```jsonc
{
  "id": 1,
  "name": "Request Submitted",
  "type": "startEvent",        // startEvent | userTask | task | exclusiveGateway | endEvent
  "laneId": 1,                 // owning lane id, or null
  "x": 69, "y": 155,           // top-left position on canvas
  "roles": [ "Requester" ],    // free-text candidate roles (userTask only)
  "requiresClaim": false,      // if true, one user must claim before acting (userTask only)
  "variables": [ /* Variable[] */ ] // startEvent (data to start) / userTask
}
```

Node kinds and their outgoing-flow rules:

- **`startEvent`**: workflow entry; circle on canvas. Carries start `variables`
  and exactly one unconditional outgoing flow, consumed automatically on start.
- **`userTask`**: human step; rounded rectangle with user-task marker. Has
  `roles`, optional `requiresClaim`, and one or more outgoing flows (each a named
  user choice). Taking a flow is the "action".
- **`task`**: automatic pass-through; rounded rectangle with AUTO marker. Exactly
  one unconditional outgoing flow, followed on entry with no user action.
- **`exclusiveGateway`**: routing node; diamond on canvas. Two or more outgoing
  flows with `condition`s plus one `isDefault`; the engine picks the first
  matching condition, else the default. No user interaction.
- **`endEvent`**: terminal; thick-ring circle. No outgoing flows.

A workflow may define multiple `startEvent` nodes. `initialEventId` is the
default. `POST /api/instances` accepts optional `startEventId` to force a
different start event; that event's variables are collected.

### SequenceFlow
A first-class directed transition between two nodes (rendered as an edge/arrow).
Ids are integers; the conventional namespacing is `sourceNodeId * 100 + n`
(e.g. node 2 -> 201, 202) but any unique integer is valid.

```jsonc
{
  "id": 201,
  "name": "Approve",           // label; empty for start/auto flows
  "sourceRef": 2,              // source node id
  "targetRef": 3,              // target node id
  "roles": [ "Manager" ],      // userTask flow: roles allowed to take it
  "variables": [ /* Variable[] */ ], // userTask flow: data captured when taken
  "condition": "amount > 1000",// exclusiveGateway flow only (nullable)
  "isDefault": false           // exclusiveGateway default flow
}
```

### Variable
Typed data attached to a start event (node) or a user-task sequence flow.

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
| `flowNode` | Flow node | Umbrella term for events, tasks, and gateways. |
| `type: "startEvent"` | None Start Event | Entry marker; thin-ring circle. Carries start `variables` (BPMN would model these as data inputs / form fields). |
| `type: "userTask"` | User Task | Human-performed activity; rounded rectangle with a user marker. |
| `type: "task"` | Abstract/automatic Task | Pass-through activity completed with no user action; closest to a BPMN Task/Service Task without an implementation. |
| `type: "exclusiveGateway"` | Exclusive Gateway (XOR) | Diamond; routes to the first outgoing flow whose condition matches, else the default flow. |
| `type: "endEvent"` | None End Event | Terminal marker; thick-ring circle. |
| `sequenceFlow` | Sequence Flow | First-class directed edge with its own id, `sourceRef`, `targetRef`. |
| `sequenceFlow.condition` | Condition Expression | Minimal `var op literal` / bare-truthy language on gateway flows. |
| `sequenceFlow.isDefault` | Default Flow | The gateway's fallback path. |
| `lane` | Lane (within a Pool) | Swimlane-style container; assignment is geometric, not a formal participant/pool model. |
| `roles` | Lane / Performer (Potential Owner) | Free-text candidate roles; not a formal resource/assignment model. |
| `variables` | Data Object / Property | Typed data captured at a start event or on a user-task flow. |
| `requiresClaim` | User Task "claim" (assignee) | Runtime-enforced single-owner locking; not a distinct BPMN element. |

### Intentional deviations from BPMN

- **Only exclusive gateways.** No parallel/inclusive/event-based gateways yet;
  branching is either a `userTask` with multiple named flows or an
  `exclusiveGateway` routed by conditions.
- **No message/timer/signal events.** Only plain (none) start and end events.
- **No pools / collaboration.** Lanes exist without a multi-party pool or message
  flow.
- **Minimal condition language.** `variable op literal` (`== != < <= > >=`) or a
  bare variable truthiness check. Anything richer (functions, boolean operators)
  is future work.
- **Integer ids, JSON (not BPMN XML).** Flow nodes and sequence flows use integer
  ids so runtime tables stay integer-keyed; the definition is JSON, not BPMN XML.
- **Node roles are enforced** at runtime (against JWT role claims); an empty
  `roles` list means open to anyone. Sequence-flow `roles` remain advisory, and
  `requiresClaim` ownership is enforced on top of the role check.

### If you add BPMN-aligned features later

- Prefer BPMN names for new node types/elements (e.g. `serviceTask`,
  `parallelGateway`, `timerStartEvent`) and add matching shapes.
- Keep the JSON tolerant: extend `loadFromObject()` (editor) and
  `WorkflowModelMigrator` (.NET) so older documents still load.
- Update `BpmnFlowNodeTypes` predicates (`IsStart`, `IsEnd`, `IsAutomatic`,
  `IsUserTask`, `IsGateway`, `IsPassThrough`) and `ValidateDefinition` together so
  the engine and editor agree.

---

## Key conventions & invariants

- **Node/lane sizing**: user/automatic tasks use `NODE_W` x `NODE_H` (170 x 64)
  rounded rectangles; start/end events use `EVENT_D` (56) circles; exclusive
  gateways use a `GATEWAY_D` (72) diamond. Lanes have minimums `MIN_LANE_W` x
  `MIN_LANE_H` (220 x 150).
- **ID generation**: `nextNodeId`, `nextLaneId`, `nextFlowId`, and
  `nextVariableId` derive new ids from existing max ids (`nextFlowId` starts at
  101).
- **Start event**: `model.initialEventId` marks the default start event (must be
  a `startEvent`). Multiple start events are allowed. Deleting the default
  reassigns it to the first remaining start event (or null).
- **Referential cleanup**: deleting a flow node also removes every sequence flow
  whose `sourceRef` or `targetRef` referenced it; deleting a lane nulls out
  `laneId` on its nodes.
- **Lane assignment is geometric**: on drag end, a node's `laneId` is set based on
  which lane its center lands in (`assignNodeLane`).
- **Load is tolerant**: `loadFromObject()` detects the schema. A document with
  `flowNodes` loads directly; a legacy document with `phases`/`steps`/`actions`
  (and `start`/`end`/`autoAdvance` types or `nextStepId`) is folded into
  `lanes`/`flowNodes`/`sequenceFlows` via `migrateLegacyToNew()`. The .NET side
  mirrors this in `WorkflowModelMigrator` using write-ignored legacy JSON shims.
- **Roles**: nodes and flows carry a free-text `roles` string array (candidate
  roles), normalized by `normalizeRoles()` (a legacy singular `role` string is
  migrated into an array). Shown as small labels on nodes and edges.
- **Requires claim**: `userTask` nodes carry a boolean `requiresClaim`
  (enforced at runtime in `WorkflowEngine/`). Hidden for other node types.
- **Automatic task / start event flow**: `task` and `startEvent` each own one
  unconditional outgoing flow, drawn as a dashed edge. The engine follows it in
  the pass-through loop with `automatic` / `start` history notes.
- **Gateway flows**: `exclusiveGateway` outgoing flows carry a `condition` or the
  `isDefault` marker; the editor shows the condition/`default` beneath the edge
  and enforces a single default per gateway.

---

## Common tasks for an AI editing this project

- **Add a new field to a node/flow/variable**: update the relevant
  `render*Inspector` function (UI), the `loadFromObject` normalizer (persistence),
  and `seedSample` if the example should show it. `save()` needs no change since
  it serializes `model` directly.
- **Add a new node type**: extend `NODE_TYPE`/`NODE_TYPE_OPTIONS` (editor), add
  matching CSS under `.node.<type>`, update geometry (`nodeSize`, `borderPoint`)
  for new shapes, and update `BpmnFlowNodeTypes` in the .NET shared model.
- **Add a new variable data type**: append to the `VARIABLE_DATA_TYPES` array and
  `WorkflowVariableTypes` (.NET).
- **Change canvas visuals**: edit the `<style>` block (CSS variables live in
  `:root`; node/lane/edge styling is grouped by class).

Keep editor changes in the single HTML file unless there is a strong reason to
split it. Preserve the editor's no-dependency, no-build nature. Runtime engine
changes belong under `WorkflowEngine/`.
