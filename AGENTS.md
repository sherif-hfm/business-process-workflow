# Business Process Workflow Editor

A single-file, dependency-free visual editor for designing business process
workflows in the browser. Users lay out **steps** (nodes) inside **phases**
(swimlane-style containers), connect steps with **actions** (directed edges),
attach typed **variables**, and save/load the whole model as JSON.

There is no build step, no server, and no external libraries. Everything runs
client-side using plain HTML, CSS, and vanilla JavaScript with inline SVG.

---

## Files

| File | Purpose |
| --- | --- |
| `workflow-editor.html` | The entire application: markup, CSS, and JS in one file. |
| `workflow.json` | A sample/exported workflow ("Purchase Request Approval") used as example data. It matches the JSON format the editor saves and loads. |
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
  an action edge between them (if the source step is `autoAdvance`, connect mode
  sets its `nextStepId` instead of creating an action).
- **Persistence**: `save()` serializes `model` to pretty-printed JSON (uses the
  File System Access API `showSaveFilePicker` when available, otherwise falls
  back to a download). Loading reads a JSON file and normalizes it through
  `loadFromObject()`, which fills in sensible defaults for missing fields.
- **Seed data**: `seedSample()` populates the "Purchase Request Approval" example
  on load.

There are no automated tests. Validation is manual, in-browser.

---

## Data model

The workflow is a plain JSON object. See `workflow.json` for a real example.

```jsonc
{
  "id": 1,
  "name": "Purchase Request Approval",
  "initialStepId": 1,          // id of the start step (nullable)
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
A node in the workflow. `type` is one of `start`, `task`, or `end`.

```jsonc
{
  "id": 1,
  "name": "Draft",
  "type": "start",             // "start" | "task" | "end"
  "phaseId": 1,                // owning phase id, or null
  "x": 69, "y": 155,           // top-left position on canvas
  "roles": [ "Requester" ],    // free-text candidate roles allowed to act on this step
  "requiresClaim": false,      // if true, one user must claim the step before acting (design-time intent; not shown for end steps)
  "autoAdvance": false,        // if true, step is a pass-through: on entry it follows nextStepId with no user action (design-time intent; hidden/forced false for end steps; mutually exclusive with requiresClaim)
  "nextStepId": null,          // direct target step id, used only when autoAdvance is true (nullable)
  "variables": [ /* Variable[] */ ], // shown for start steps: data to start the workflow
  "actions":   [ /* Action[]   */ ]  // outgoing edges (end steps have none); kept but hidden while autoAdvance is true
}
```

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
Typed data attached to a start step or an action.

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

## Key conventions & invariants

- **Node/phase sizing**: steps are a fixed `NODE_W` x `NODE_H` (170 x 64).
  Phases have minimums `MIN_PHASE_W` x `MIN_PHASE_H` (220 x 150).
- **ID generation**: `nextStepId`, `nextPhaseId`, `nextActionId`, and
  `nextVariableId` derive new ids from existing max ids. Action ids are namespaced
  per step (`step.id * 100 + n`).
- **Start step**: `model.initialStepId` marks the entry point. Deleting that step
  reassigns it to the first remaining step (or null).
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
- **Requires claim**: steps carry a boolean `requiresClaim` (design-time intent
  only; enforcement is a runtime concern that lives outside this editor). It
  defaults to `false`, is hidden for `end` steps, and is forced `false` when a
  step's type becomes `end`.
- **Auto-advance**: steps carry a boolean `autoAdvance` and a nullable
  `nextStepId` (design-time intent only). When `autoAdvance` is `true` the step is
  a pass-through that follows `nextStepId` with no user action: its `actions` are
  preserved but hidden (in the inspector and on the canvas) and a single dashed
  "auto" edge is drawn to `nextStepId` instead. It defaults to `false`, is
  hidden/forced `false` for `end` steps, and is mutually exclusive with
  `requiresClaim` (enabling it forces `requiresClaim` to `false`, and the roles /
  requires-claim controls are hidden while it is on). Nodes show an `AUTO` marker
  in their header. `nextStepId` is set via the "Goes to step" inspector dropdown
  or connect mode.

---

## Common tasks for an AI editing this project

- **Add a new field to a step/action/variable**: update the relevant
  `render*Inspector` function (UI), the `loadFromObject` normalizer (persistence),
  and `seedSample` if the example should show it. `save()` needs no change since
  it serializes `model` directly.
- **Add a new step type**: extend the `selectField("Type", [...])` options in
  `renderStepInspector`, and add matching CSS under `.node.<type> rect`.
- **Add a new variable data type**: append to the `VARIABLE_DATA_TYPES` array.
- **Change canvas visuals**: edit the `<style>` block (CSS variables live in
  `:root`; node/phase/edge styling is grouped by class).

Keep everything in the single HTML file unless there is a strong reason to split
it. Preserve the no-dependency, no-build nature of the project.
