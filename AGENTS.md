# Business Process Workflow Editor

A single-file, dependency-free visual editor for designing business process
workflows in the browser. Users lay out **flow nodes** inside **lanes**
(swimlane-style containers), connect them with **sequence flows** (directed
edges), attach typed **variables**, and save/load the whole model as JSON.

The model is a simplified, BPMN 2.0-aligned subset. Flow nodes are typed as
`startEvent`, `userTask`, `task`, `serviceTask`, `exclusiveGateway`, or
`endEvent`, drawn with BPMN-style shapes (event circles, task rounded
rectangles, gateway diamonds).
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
  definition validation, sequence-flow condition evaluation
  (`SequenceFlowConditionEvaluator`, NCalc), service interfaces, repository
  ports, and DI extension.
- `src/WorkflowEngine.Infrastructure` - Infrastructure layer: EF Core,
  PostgreSQL/Npgsql, JSONB mapping, migrations, repository implementations,
  scriptTask JavaScript execution (`JintScriptEvaluator`, sandboxed Jint), and
  unit of work.
- `src/WorkflowEngine.Shared` - shared DTOs and C# model for the editor JSON.
- `src/WorkflowEngine.Ui` - Blazor Server UI that calls the API through a typed
  `HttpClient`.

Storage follows the hybrid design:

- Workflow definitions are immutable/versioned JSONB snapshots in
  `workflow_definitions`. Each row also carries `WorkflowKey`, an indexed integer
  stamped from the editor JSON model `id` on every save; it is the same across all
  versions of a workflow, so it acts as a stable cross-version key for instance
  search (see the workflow key search below).
- Runtime state is normalized in `workflow_instances`, `instance_variables`, and
  `instance_history`. These tables keep integer ids and their original column
  names (`CurrentStepId`, `ActionId`, `FromStepId`, `ToStepId`,
  `SourceActionId`); the columns now simply carry flow-node / sequence-flow ids,
  so no database migration was needed for the BPMN rename.
- **Denormalized current-node columns.** `workflow_instances` also carries
  `CurrentNodeName`, `CurrentNodeExternalId` (nullable), `CurrentNodeType`,
  `CurrentNodeRoles` (Postgres `text[]`),
  and `CurrentRequiresClaim`, stamped from the resting flow node on every node
  change (`AddInstanceAsync`, `TakeFlowAsync`, and each `ResolvePassThroughAsync`
  hop via `UpdateInstanceNodeAsync`; claim/unclaim/cancel leave them untouched).
  These let the list and inbox read paths filter, sort, and page entirely in SQL
  without parsing the definition JSONB. The inbox query
  (`WorkflowRuntimeRepository.ListInboxAsync`) is parameterized raw SQL:
  `Status = 'running' AND CurrentNodeType = 'userTask'` plus a claim/role
  predicate (`ClaimedBy = @me`, or case-insensitive `CurrentNodeRoles`
  overlap with the actor roles while excluding tasks claimed by someone else),
  ordered by `UpdatedAt DESC, Id DESC`. Indexes:
  `(Status, CurrentNodeType, UpdatedAt)`, `(Status, UpdatedAt, Id)`, and a GIN
  index on `CurrentNodeRoles`. The action flags (`CanClaim`/`CanAct`/
  `ClaimedByMe`) are computed per page row from these columns. Pre-existing rows
  are backfilled once at dev startup (`DatabaseInitializer.BackfillCurrentNodeAsync`,
  guarded by an empty `CurrentNodeType`).
- Instance transitions run in a database transaction and lock the instance row
  with `SELECT ... FOR UPDATE`; there is no in-memory run engine state.
- **Pass-through routing** (`ResolvePassThroughAsync`): `startEvent`,
  `messageStartEvent`, automatic `task`, `serviceTask`, `scriptTask`,
  `exclusiveGateway`, and `errorBoundaryEvent` nodes are resolved in the same
  transaction until the instance rests on a `userTask` or
  `intermediateMessageCatchEvent`, or terminates on an `endEvent`/`errorEndEvent`.
  A hop limit (`flowNodes.Count + 1`) guards against cycles. History rows are
  written with a `start`, `messageStart`, `automatic`, `service`, `script`,
  `gateway`, `boundary`, `error`, or `message` note.
- **Service tasks** call an external REST endpoint during the pass-through hop
  (`IServiceTaskInvoker` / `HttpServiceTaskInvoker`, a typed `HttpClient`). The
  URL, header values, and JSON body are built by substituting `${var}`
  placeholders from instance variables (`ServiceTaskTemplating`); on a `2xx`
  response the configured `outputMappings` extract dotted-path values and write
  them to instance variables (latest write wins). The call is synchronous inside
  the locked transaction with a bounded `timeoutSeconds` and **no retries**; on a
  non-2xx/timeout/network failure the engine looks up an attached
  `errorBoundaryEvent` (see Error events). If one exists the token routes out the
  boundary's single error flow (and an optional `statusVariable` still receives
  the HTTP status, 0 on transport error, so the error path can branch); if none
  is attached the transition fails with a `WorkflowDomainException` (rollback +
  400). Definitions are validated (`ValidateDefinition`) for a URL, allowed
  method, positive timeout, and complete output mappings.
- **Script tasks** mutate process variables during the pass-through hop in one of
  two authoring modes (`scriptFormat`), never both at once:
  - `ncalc` (default): `assignments` are NCalc expressions evaluated **in order**
    against a running overlay, so a later assignment sees an earlier one's write
    within the same node.
  - `javascript`: the `script` body runs in a sandboxed Jint `Engine` (no
    `AllowClr()` - no filesystem/network/reflection) with a single bound
    `execution` host object: `execution.getVariable(name)`,
    `execution.setVariable(name, value)`, `execution.getVariables()`,
    `execution.hasVariable(name)`. A script reads its own writes within the same
    execution (an in-memory overlay, same as the ncalc path). Execution is bounded
    by `ScriptOptions` (`WorkflowScript` config section: `TimeoutSeconds`,
    `MaxStatements`, `MemoryBytes`, defaults 5s / 100,000 / 8 MB), enforced both by
    Jint's own execution constraints and a hard wall-clock
    `CancellationTokenSource.CancelAfter` backstop (a few Jint built-ins bulk-process
    in a single CLR call that bypasses per-step constraint checks -
    [sebastienros/jint#2486](https://github.com/sebastienros/jint/issues/2486)).
  In both modes, every write is coerced to the target's declared `dataType`/
  `isArray` (`CoerceScriptValue`) and must target a **declared process variable**
  (`model.variables`) - an undeclared `setVariable`/assignment target throws
  `WorkflowDomainException`, rolling back the transition. Each distinct target
  variable's `validation` rule is re-checked against the in-memory overlay
  (before persistence) so an error-boundary catch leaves nothing half-written.
  A script/assignment/validation failure routes out an attached
  `errorBoundaryEvent` like a service-task failure; with no boundary the
  transition fails (rollback + 400). `IScriptEvaluator`
  (`JintScriptEvaluator` in the infrastructure layer) also parse-checks a
  `javascript` body at author time (`Engine.PrepareScript`, no execution) via
  `ValidateDefinition`.
- **Exclusive gateways** evaluate outgoing flows: the first flow whose
  `condition` is true wins; otherwise the `isDefault` flow is taken; if neither
  matches the transition fails. Conditions are evaluated by
  `SequenceFlowConditionEvaluator` using [NCalc](https://github.com/ncalc/ncalc):
  instance variables are exposed as parameters and the full NCalc grammar is
  supported (comparisons `== != < <= > >=`, boolean `and`/`or`, arithmetic,
  parentheses, quoted string literals). String comparisons are case-insensitive;
  the result is coerced to a boolean so a bare variable name still works as a
  truthiness check (non-zero numbers / non-empty strings are truthy). An optional
  `${ ... }` wrapper is stripped, and invalid or unresolvable expressions
  evaluate to `false`. Conditions are also parse-checked when a definition is
  created/updated. Beyond NCalc's built-ins (`Min`, `Max`, `if`, `in`, math
  helpers), `SequenceFlowConditionEvaluator` registers **custom helper functions**
  (case-insensitive names, wired once in `CreateExpression` so evaluation and
  parse-checking agree): `Length(s)` / `Len(s)`, `IsNullOrEmpty(s)`,
  `IsNullOrWhiteSpace(s)`, `Contains(s, sub)`, `StartsWith(s, prefix)`,
  `EndsWith(s, suffix)`, `Lower(s)`, `Upper(s)`, `Trim(s)`, and
  `IsMatch(s, pattern)` (regex match, case-insensitive, bounded execution time as
  a ReDoS guard). Substring/regex matching is case-insensitive. These helpers work
  anywhere NCalc runs (gateway conditions and variable `validation` rules). A
  helper called with too few arguments (or a mistyped name) is treated as unknown;
  since author-time `IsValid` only parse-checks grammar, such typos are not flagged
  and the expression simply evaluates to `false` at runtime.
- `requiresClaim` **is** enforced at runtime: such a `userTask` must be claimed
  (`POST /claim`) before its flows are available or can be taken, only the
  claiming user may act, and the claim is released on transition. `unclaim`
  clears it. Claim ownership is tracked on `workflow_instances.claimed_by`
  (users default to `anonymous` when unspecified).
- **Claim inheritance** (`claimMode` on a `requiresClaim` `userTask`) lets a
  resting task auto-claim to a prior actor instead of re-prompting, resolved from
  `instance_history` (each taken flow logs `PerformedBy`):
  `fresh` (default) leaves it unclaimed; `previous` inherits the actor of the
  most recent user action anywhere in the instance; `fromNode` inherits the actor
  of the most recent user action taken from `inheritClaimFromNodeId` (another
  user-task node). If no matching history exists yet (e.g. the first time the
  task is reached) it falls back to unclaimed, so a retry loop claims once then
  stays claimed. Applied by `ApplyClaimInheritanceAsync` after the pass-through
  resolves in `StartInstanceAsync`/`TakeFlowAsync`, inside the locked
  transaction. Inheritance does not re-check roles (history stores no roles); the
  inherited user still passes the normal role check when they act.
  `ValidateDefinition` requires `requiresClaim` for a non-`fresh` mode and a valid
  `userTask` reference for `fromNode`.
- Required `variables` are validated when starting an instance (chosen start
  event variables) and when taking a sequence flow (flow variables); missing
  required values are rejected.
- Instances move through `Running`, `Completed` (on entering an `endEvent`),
  `Faulted` (on entering an `errorEndEvent`), and `Cancelled` (`POST /cancel`)
  statuses.
- **Error events.** An `errorBoundaryEvent` is attached to a `serviceTask` or
  `scriptTask` (`attachedToRef`) and catches that task's runtime failures
  (HTTP non-2xx/timeout/network, or a script/assignment/validation error): the
  token routes out the boundary's single outgoing **error** flow instead of
  failing the transition, with an optional `errorVariable` capturing the failure
  reason. With no boundary attached, a failure throws a
  `WorkflowDomainException` (rollback + 400) - the historical no-boundary
  default. A boundary is pass-through (it auto-advances its error flow, history
  note `error` then `boundary`) and may have no incoming flows; at most one
  boundary per host. An `errorEndEvent` is a terminal event (no outgoing flows)
  that ends the instance with the `Faulted` status; it is typically reached via
  a boundary's error path, directly or through a handler task. The legacy
  `serviceTask.onError` (fail|continue) field has been removed in favor of
  boundary events (old definitions load tolerantly; the dropped `onError` value
  is ignored, and behavior is the no-boundary default = fail).
- **Intermediate message catch events.** An `intermediateMessageCatchEvent` is a
  resting node (like a `userTask`) that waits for an external system to deliver a
  message via `POST /api/instances/{id}/message`; it then maps the message payload
  into instance variables and advances down its single outgoing flow. Correlation
  is by instance id only (the instance must be `Running` and currently resting on
  the catch node); there is no cross-instance message-name/signal matching. The
  delivery caller authenticates against the catch node's expected `clientId` /
  `clientSecret` (sent as `X-Client-Id` / `X-Client-Secret` headers) and a required
  custom header named by `headerName` whose value must equal `headerValue`; an
  optional `headerValidation` NCalc rule is evaluated with the incoming header
  value bound as `header` (plus instance variables + `sys.*`/`config.*`/`setting.*`
  context) and must be truthy. All credential/header fields are `${var}`-templatable
  (`ServiceTaskTemplating.SubstituteScalar`), so a secret can be sourced from
  `${config.*}` / `${setting.*}` to stay out of the versioned definition JSON.
  `outputMappings` (`{variable, path, required}`) extract dotted-path values from the inbound
  JSON message body and write them to instance variables raw/uncoerced (mirrors a
  `serviceTask`'s `ApplyServiceOutputsAsync`; targets need not be declared); a
  `required` mapping whose path is unresolvable rejects the delivery with a 400
  before any variables are written. The
  message endpoint is `AllowAnonymous` (it does not use the user JWT); a client
  id/secret mismatch throws `WorkflowUnauthorizedException` (401), while a header
  problem (missing/mismatch/validation failure) or a not-running / not-waiting
  instance throws `WorkflowDomainException` (400). The resolved client id is
  recorded as `performedBy`/`sys.user` for attribution.
  **Credential/header resolution context.** The `clientId`/`clientSecret`/
  `headerName`/`headerValue` templates are resolved against stored instance
  variables overlaid with `config.*`/`setting.*` and the `sys.*` entries an
  unverified caller cannot influence (`sys.now`, `sys.today`, `sys.instanceId`,
  `sys.workflowId`, `sys.nodeId`, `sys.nodeName`) - **`sys.user` and `sys.roles`
  are deliberately excluded**, since for an anonymous delivery they come from the
  unverified `X-Client-Id` header and would otherwise let a caller satisfy a
  credential templated as `${sys.user}`. (The `headerValidation` NCalc rule, run
  only after the caller is authenticated, may still reference `sys.user`.) Secrets
  in credential/header fields should be sourced from `${config.*}`/`${setting.*}`,
  never written as literals in the definition JSON.
  **Response shape.** The endpoint returns a slim `MessageDeliveryAckDto`
  (`Id`, `CurrentNodeId`, `CurrentNodeName`, `CurrentNodeExternalId`, `Status`,
  `UpdatedAt`) - not the full `InstanceDetailDto` - so a node-credentialed webhook
  caller cannot read the workflow definition (which may contain other nodes'
  literal secrets) or the instance's stored variables/history.
  **Idempotency.** There is no delivery idempotency key: a webhook that retries
  after a successful delivery gets a `400` ("not currently waiting for a message")
  because the instance has already advanced off the catch node. Integrators should
  deduplicate at the source; a future idempotency-key mechanism could address this.
  No timeout escape hatch exists yet (a waiting instance waits indefinitely, like
  an unclaimed `userTask`); a future timer boundary event could address that.
- **Message start events.** A `messageStartEvent` is an entry point (like a
  `startEvent`) that is started by an external system via
  `POST /api/workflows/{workflowKey}/message-start` rather than by a user. It is
  **system-only**: `IsStart` is intentionally false, so the user `POST /api/instances`
  path rejects it and the Blazor "pick a start event" page does not list it. It
  carries start `variables` (typed/required/validated like a `startEvent`'s) and a
  `message` config (the same `MessageCatchModel` as an intermediate catch:
  `clientId`/`clientSecret`/`headerName`/`headerValue`/`headerValidation`/
  `outputMappings`, plus an optional `idempotencyVariable`). The caller addresses
  the workflow by its stable cross-version `workflowKey` (the latest published
  version is resolved); an optional `?startEvent={externalId}` selects a specific
  message-start event when the workflow has more than one (400 if ambiguous and
  none is given). Auth mirrors the intermediate catch: client id/secret via
  `X-Client-Id`/`X-Client-Secret` (401 on mismatch) + required header (400 on
  missing/mismatch/validation failure), all `${var}`-templatable against an
  instance-less context (`config.*`/`setting.*` + non-caller-influenced `sys.*`;
  no `sys.user`/`sys.roles`/`sys.instanceId` since there is no caller/instance
  yet). `outputMappings` extract dotted-path values from the inbound JSON body
  and feed them as the start-variable values, so declared start `variables`
  (required/defaults/NCalc `validation`) still apply. It is pass-through: after
  creating the instance the engine auto-advances off it (history note
  `messageStart`), like a `startEvent`. **Idempotency.** When `idempotencyVariable`
  names one of the node's declared start variables, the engine serializes
  concurrent retries with a transaction-scoped `pg_advisory_xact_lock` keyed on
  `(workflowKey, hashtext(keyValue))` and, before creating an instance, searches
  for an existing instance of the workflowKey already carrying that key value
  (via the variable-search path, any status); if found it returns that instance's
  ack (no duplicate). The key must be passed via the `Idempotency-Key` or `X-Idempotency-Key`
  request header (else 400).
  A slim `MessageStartAckDto` (`InstanceId`, `CurrentNodeId`, `CurrentNodeName`,
  `CurrentNodeExternalId`, `Status`, `CreatedAt`) is returned (never the full
  definition/variables/history, since the endpoint is `AllowAnonymous`). No DB
  migration is needed (config is JSONB; the type fits the 32-char `CurrentNodeType`).
- **Node roles are enforced** at runtime for `userTask` nodes. The caller's
  identity and roles come from a validated JWT (name + role claims), not from
  request fields. A `userTask` with a non-empty `roles` list can only be
  claimed/acted on by a caller holding one of those roles; an empty `roles` list
  is open to anyone. `GetAvailableFlowsAsync` hides flows when the role does not
  match; `ClaimAsync`/`TakeFlowAsync` reject with a `WorkflowDomainException`.
  Sequence-flow `roles` are still advisory (not enforced), but `userTask` flows
  can also carry a `condition` (NCalc) and an `isDefault` flag: the engine filters
  visible actions in `GetAvailableFlowsAsync` and re-checks the condition before
  executing `TakeFlowAsync`. In addition, a `userTask` node may itself carry a
  `condition` (NCalc) that acts as a **visibility gate**: when the condition is
  false, the instance is hidden from the actor's inbox, no flows are returned by
  `GetAvailableFlowsAsync`, and `ClaimAsync`/`TakeFlowAsync` reject. The instance
  still rests on the node; the condition does not change routing.
- Authentication: the API validates a bearer JWT (`Microsoft.AspNetCore.Authentication.JwtBearer`)
  using a shared symmetric key (`Jwt:Key`, dev only) and requires it on the
  `/api/instances` group. The Blazor UI mints its own token from the `/token`
  page (`DevTokenFactory` + `TokenState`) so a tester can switch user/roles on
  the fly; `AuthTokenHandler` attaches it as a `Bearer` header. For production,
  swap `AddJwtBearer` to a real OIDC identity provider and remove the UI minting
  page.

Definitions are versioned: `POST /api/workflows` creates v1, `PUT
/api/workflows/{id}` creates a new immutable version, and only a *published*
definition can start instances. Each version row gets a fresh `Id`, but they
share the JSON model `id` via the denormalized `WorkflowKey` column
(`WorkflowSummaryDto`/`WorkflowDetailDto` expose it as `workflowKey`), which is
what the cross-version `workflowKey` instance search matches.

### HTTP API

- `WorkflowDefinitionEndpoints` (`/api/workflows`): `GET /` (latest per
  definition), `GET /{id}`, `POST /` (create), `PUT /{id}` (new version),
  `POST /{id}/publish`, `DELETE /{id}`,
  `POST /{workflowKey}/message-start` (start a new instance via a messageStartEvent;
  `AllowAnonymous` — auth is the node's client id/secret + required header, not
  the user JWT; returns a slim `MessageStartAckDto`).
- `WorkflowInstanceEndpoints` (`/api/instances`): `POST /` (start; optional
  `startEventId`), `GET /?status=&instanceId=&workflowId=&workflowKey=&nodeId=&nodeExternalId=&var=&page=&pageSize=` (paged),
  `GET /inbox?instanceId=&workflowId=&workflowKey=&nodeId=&nodeExternalId=&var=&page=&pageSize=` (paged, actor-scoped), `GET /{id}`,
  `GET /{id}/flows` (available sequence flows), `POST /{id}/claim`,
  `POST /{id}/unclaim`, `POST /{id}/flows/{flowId}` (take a flow),
  `POST /{id}/message` (deliver a message to an `intermediateMessageCatchEvent`;
  `AllowAnonymous` - auth is the node's client id/secret + required header, not
  the user JWT; body is the raw JSON message payload; returns a slim
  `MessageDeliveryAckDto` (no definition/variables/history), 401 on a client
  id/secret mismatch, 400 on a header problem or when not running/waiting),
  `POST /{id}/cancel`. The two list endpoints return
  `PagedResult<T>` (`Items`, `Page`, `PageSize`, `TotalCount`); `page` defaults
  to 1 and `pageSize` defaults to 50, clamped to a max of 200. Paging is
  offset-based; results are ordered by `UpdatedAt DESC, Id DESC` so the
  repository can later switch to keyset paging without an API change.
  **Inbox visibility conditions.** When any `userTask` node reached by the inbox
  candidate set carries a `condition`, the inbox switches to exact-count post-filtering:
  it fetches the full actor-filtered candidate set, evaluates the NCalc condition
  against each instance's variables, and re-pages the surviving rows. The fast
  SQL-only path is preserved when no relevant `userTask` has a `condition`.
- **Variable search.** Both list endpoints accept repeated `var=name:value`
  query params (split on the first `:`, so values may contain `:`). Each pair is
  an exact, case-insensitive match on an instance variable's scalar value; when
  several are supplied they are AND-combined. Matching is compiled to one
  correlated `EXISTS` over `instance_variables` per pair
  (`lower("ValueJson" #>> '{}') = lower(@value)`), all values bound as
  parameters. The inbox filter is additive on top of the actor role/claim scope.
  For the list endpoint the `var` filter combines with `status`. Malformed
  entries (missing `:` or empty name) are rejected via `WorkflowDomainException`.
  Array/object variables never match, and value ranges/operators are out of
  scope. An `instance_variables (VariableName, InstanceId)` index backs the
  lookup.
- **Instance id / workflow id search.** Both list endpoints accept optional
  integer `instanceId=` and `workflowId=` query params: exact matches on the
  instance primary key (`w."Id" = @instanceId`) and the owning definition
  (`w."WorkflowDefinitionId" = @workflowId`), both values bound as parameters. A
  null / absent value applies no filter, and they AND-combine with the other
  filters (and the inbox actor scope). `instanceId` overlaps with `GET /{id}` but
  is convenient inside the shared filter/paging UI; `workflowId` scopes the list
  to a single workflow definition version. Both are backed by existing indexes
  (the primary key and the `WorkflowDefinitionId` foreign-key index).
- **Workflow key search.** Both list endpoints accept an optional integer
  `workflowKey=` query param that matches the stable, cross-version workflow key
  (`workflow_definitions.WorkflowKey`, stamped from the editor JSON model `id`).
  It is compiled to a correlated `EXISTS` joining each instance to its definition
  (`d."Id" = w."WorkflowDefinitionId" AND d."WorkflowKey" = @workflowKey`, value
  bound as a parameter). Unlike `workflowId` (a single version's row id), the key
  is the same on every version, so `workflowKey` returns instances across ALL
  versions of a workflow. A null / absent value applies no filter, and it
  AND-combines with the other filters (and the inbox actor scope). The key is
  stored as-is from the JSON `id` on every save and is not validated for
  uniqueness, so if two definitions share a JSON `id` the search spans both. A
  `WorkflowKey` index on `workflow_definitions` backs the lookup.
- **Current-node id search.** Both list endpoints accept an optional integer
  `nodeId=` query param, an exact match on the denormalized `CurrentStepId`
  column (`w."CurrentStepId" = @nodeId`, value bound as a parameter). A null /
  absent value applies no filter. It AND-combines with `status` /
  `nodeExternalId` / `var` (and the inbox actor scope). Because the node id is a
  version-scoped integer, it is most meaningful combined with a specific
  workflow; the pre-existing `CurrentStepId` index backs the lookup.
- **Current-node externalId search.** Both list endpoints accept an optional
  `nodeExternalId=` query param, an exact, case-insensitive match on the
  denormalized `CurrentNodeExternalId` column
  (`lower(w."CurrentNodeExternalId") = lower(@nodeExternalId)`, value bound as a
  parameter). Empty/whitespace input applies no filter. It AND-combines with
  `status` / `var` (and the inbox actor scope). Because externalId is a
  version-stable integration key it survives definition re-versioning (unlike the
  integer node id). A `(Status, CurrentNodeExternalId)` index backs the lookup.
- `WorkflowDomainException` maps to problem responses for invalid operations
  (unpublished workflow, missing variable, bad claim, unavailable flow,
  gateway with no matching/default flow, etc.).

### Blazor UI pages

- `/workflows` (`Workflows.razor`) - list definitions.
- `/workflows/{id}/start` (`StartInstance.razor`) - pick a start event, fill its
  variables, and launch an instance.
- `/instances` (`Instances.razor`) - list instances, filterable by status, by
  instance id (`instanceId=`), workflow id (`workflowId=`), workflow key
  (`workflowKey=`), node id (`nodeId=`), node external id (`nodeExternalId=`), and
  by variables (a comma-separated `name:value` box mapped to repeated `var=`
  params).
- `/inbox` (`Inbox.razor`) - actor-scoped inbox, with the same instance id,
  workflow id, workflow key, node id, node external id, and comma-separated
  `name:value` variable filter boxes.
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
  "variables": [ /* Variable[] */ ], // process-level declarations (see Process variables)
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
  "externalId": "LANE_SUBMISSION", // optional free-form integration key (nullable)
  "x": 29, "y": 95,            // top-left position on canvas
  "w": 1082, "h": 172          // width / height (min 220 x 150)
}
```

### FlowNode
A node in the workflow. `type` is one of `startEvent`, `userTask`, `task`,
`serviceTask`, `scriptTask`, `exclusiveGateway`, `endEvent`, `errorEndEvent`,
`errorBoundaryEvent`, `intermediateMessageCatchEvent`, or `messageStartEvent`.

```jsonc
{
  "id": 1,
  "name": "Request Submitted",
  "externalId": "TASK_SUBMIT", // optional free-form integration key (nullable)
  "type": "startEvent",        // startEvent | userTask | task | serviceTask | scriptTask | exclusiveGateway | endEvent | errorEndEvent | errorBoundaryEvent | intermediateMessageCatchEvent | messageStartEvent
  "laneId": 1,                 // owning lane id, or null
  "x": 69, "y": 155,           // top-left position on canvas
  "roles": [ "Requester" ],    // free-text candidate roles (userTask only)
  "requiresClaim": false,      // if true, one user must claim before acting (userTask only)
  "claimMode": "fresh",        // userTask + requiresClaim: fresh | previous | fromNode (claim inheritance)
  "inheritClaimFromNodeId": null, // fromNode mode only: user-task node whose claimant is reused
  "condition": null,           // userTask only: NCalc visibility gate; null = always visible
  "variables": [ /* Variable[] */ ], // startEvent / messageStartEvent (data to start) / userTask
  "service": { /* ServiceTaskConfig */ }, // serviceTask only (REST call config)
  "scriptFormat": "ncalc",     // scriptTask only: ncalc | javascript
  "assignments": [ /* Assignment[] */ ], // scriptTask + scriptFormat "ncalc" only (see Assignment)
  "script": null,              // scriptTask + scriptFormat "javascript" only (see below)
  "attachedToRef": null,       // errorBoundaryEvent only: host serviceTask/scriptTask id
  "errorVariable": null,       // errorBoundaryEvent only, optional: captures the failure reason
  "message": { /* MessageCatchConfig */ } // intermediateMessageCatchEvent only (see MessageCatchConfig)
}
```

### Assignment
An ordered variable write performed by a `scriptTask` node during its pass-through
hop when `scriptFormat` is `"ncalc"` (the default). Each `expression` is NCalc,
evaluated against the current instance variables overlaid with `sys.*` / `config.*`
context (use `[sys.user]` bracket syntax for dotted context names); the typed
result is coerced to the target variable's declared `dataType` and persisted.
Assignments run in list order against a running overlay, so a later one can
reference an earlier one's write in the same list.

```jsonc
{
  "variable": "total",         // must reference a declared process variable name
  "expression": "amount * 1.1" // NCalc expression; parse-checked at author time
}
```

### Script (JavaScript scriptFormat)
When `scriptFormat` is `"javascript"`, `flowNode.script` is a JavaScript body run
by [Jint](https://github.com/sebastienros/jint) in a sandboxed `Engine` (no CLR
access) with a single bound `execution` host object:

```jsonc
{
  "scriptFormat": "javascript",
  "script": "var orderAmount = execution.getVariable('orderAmount');\nvar tax = orderAmount * 0.15;\nvar total = orderAmount + tax;\nexecution.setVariable('tax', tax);\nexecution.setVariable('total', total);"
}
```

- `execution.getVariable(name)` - reads a stored instance variable or `sys.*`/
  `config.*` context value (same map as NCalc assignments), marshalled to a native
  JS value (number/string/boolean/array/object).
- `execution.setVariable(name, value)` - writes a **declared process variable**
  (`model.variables`); the JS value is marshalled back to JSON and coerced to the
  target's declared `dataType`/`isArray`, exactly like an NCalc assignment. An
  undeclared target throws (rolls back the transition).
- `execution.getVariables()` - a snapshot object of all currently visible
  variables (property access, e.g. `vars.amount`).
- `execution.hasVariable(name)` - existence check.
- A script reads its own writes within the same execution (an in-memory overlay),
  so `execution.getVariable('tax')` after `setVariable('tax', ...)` sees the new
  value even before it is persisted.

`assignments` and `script` are mutually exclusive per `scriptFormat`; the editor
and `ValidateDefinition` both enforce that only the active mode's field is
populated.

Node kinds and their outgoing-flow rules:

- **`startEvent`**: workflow entry; circle on canvas. Carries start `variables`
  and exactly one unconditional outgoing flow, consumed automatically on start.
- **`userTask`**: human step; rounded rectangle with user-task marker. Has
  `roles`, optional `requiresClaim`, and one or more outgoing flows (each a named
  user choice). Taking a flow is the "action".
- **`task`**: automatic pass-through; rounded rectangle with AUTO marker. Exactly
  one unconditional outgoing flow, followed on entry with no user action.
- **`serviceTask`**: automatic REST call; rounded rectangle with SVC marker.
  Carries a `service` config (method, URL, headers, JSON body, timeout, output
  mappings) and exactly one unconditional outgoing flow. On entry the engine
  calls the endpoint, writes response fields into instance variables, then
  follows the flow. No user action.
- **`scriptTask`**: automatic variable mutation; rounded rectangle with SCRIPT
  marker. Carries `scriptFormat` plus either `assignments` (ncalc) or `script`
  (javascript, run by Jint) and exactly one unconditional outgoing flow. On entry
  the engine runs the active mode, coerces each write to the target process
  variable's `dataType`, persists it (append-only; last write wins), then follows
  the flow. No user action. Each target variable's `validation` rule is
  re-checked after the writes; a failure rolls back the transition. The first
  automatic node type that carries authored data (`assignments`/`script`), so
  `ApplyNodeInvariants` has a dedicated case that preserves them.
- **`exclusiveGateway`**: routing node; diamond on canvas. Two or more outgoing
  flows with `condition`s plus one `isDefault`; the engine picks the first
  matching condition, else the default. No user interaction.
- **`endEvent`**: terminal; thick-ring circle. No outgoing flows.
- **`errorEndEvent`**: terminal; thick-ring circle with an error glyph. No
  outgoing flows. Entering it ends the instance with the `Faulted` status
  (vs `Completed` for a plain `endEvent`). Typically reached via an
  `errorBoundaryEvent`'s error path, directly or through a handler task.
- **`errorBoundaryEvent`**: an error catch attached to a `serviceTask` or
  `scriptTask` (`attachedToRef`); a small double-ring circle drawn on the
  host's border. When the host fails at runtime (HTTP non-2xx/timeout/network,
  or a script/assignment/validation error) the token routes out the boundary's
  single outgoing **error** flow instead of failing the transition, with an
  optional `errorVariable` capturing the failure reason. With no boundary
  attached, a failure fails the transition (rollback + 400). A boundary is
  pass-through (auto-advances its error flow) and may have no incoming flows;
  at most one boundary per host.
- **`intermediateMessageCatchEvent`**: a resting node (like a `userTask`) that
  waits for an external system to deliver a message via
  `POST /api/instances/{id}/message`; a thin double-ring circle with an envelope
  glyph on canvas. Carries a `message` config (clientId, clientSecret, headerName,
  headerValue, headerValidation, outputMappings) and exactly one unconditional
  outgoing flow. The instance rests on it until a matching message is delivered;
  the caller authenticates against the templated client credentials + required
  header, the payload is mapped into instance variables via `outputMappings`
  (raw, like a service task), and the engine advances down the flow. No user
  action. Correlation is by instance id only (no cross-instance signal/message
  matching). No timeout escape hatch yet.
- **`messageStartEvent`**: an entry point (like a `startEvent`) started by an
  external system via `POST /api/workflows/{workflowKey}/message-start`; a thin
  single-ring circle with an envelope glyph on canvas. Carries start `variables`
  (typed/validated like a `startEvent`) and a `message` config (the same shape as
  an intermediate catch, plus an optional `idempotencyVariable`) and exactly one
  unconditional outgoing flow. System-only: it is not a `startEvent` (`IsStart` is
  false), so it cannot be started via `POST /api/instances` and does not appear in
  the Blazor start picker. The engine creates the instance and auto-advances off
  it (pass-through, history note `messageStart`). The caller authenticates against
  the templated client credentials + required header; `outputMappings` feed the
  start variables (so required/defaults/validation still apply). An optional
  `idempotencyVariable` dedupes retried webhooks (no duplicate instance).

A workflow may define multiple `startEvent` and/or `messageStartEvent` nodes.
`initialEventId` is the default **user** start event (optional — a workflow whose
only entry is a `messageStartEvent` leaves it null); it must reference a
`startEvent` when set. `POST /api/instances` accepts optional `startEventId` to
force a different start event; that event's variables are collected.

### SequenceFlow
A first-class directed transition between two nodes (rendered as an edge/arrow).
Ids are integers; the conventional namespacing is `sourceNodeId * 100 + n`
(e.g. node 2 -> 201, 202) but any unique integer is valid.

```jsonc
{
  "id": 201,
  "name": "Approve",           // label; empty for start/auto flows
  "externalId": "FLOW_MGR_APPROVE", // optional free-form integration key (nullable)
  "sourceRef": 2,              // source node id
  "targetRef": 3,              // target node id
  "roles": [ "Manager" ],      // userTask flow: advisory roles shown in the editor
  "variables": [ /* Variable[] */ ], // userTask flow: data captured when taken
  "condition": "amount > 1000",// userTask / exclusiveGateway flow only (nullable)
  "isDefault": false           // userTask / exclusiveGateway default flow
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
  "required": true,
  "defaultValue": null,        // optional vars only: value (or ${...} template) persisted when none supplied
  "validation": null           // optional NCalc rule checked at start / flow-take (nullable)
}
```

`defaultValue` applies to **optional** variables only (the editor hides it when
`required` is checked). When a value is not supplied at start / flow-take, the
default is coerced to the declared `dataType`/`isArray` and **persisted as an
instance variable** exactly like a supplied value, so gateways, service tasks,
history, and variable search all see it. Required variables still must be
supplied. Names beginning with the reserved `sys.` / `config.` prefixes are
rejected by `ValidateDefinition` (see context sources below).

A **string** `defaultValue` (or each string element of an array default) may
contain `${...}` placeholders. They are resolved just before the default is
coerced and persisted, against a map of the other collected values for that scope
overlaid with read-only `sys.*` / `config.*` context (context wins on collision),
so a default of `"${sys.user}"` records the caller and `"${amount}"` copies
another supplied value. Defaults are resolved in list order, so a later default
can reference an earlier one; a missing placeholder resolves to an empty string
(same rule as service-task `SubstituteScalar`). This resolution happens in
`ResolveAndValidateVariables` (`WorkflowEngineService`) at both start and
flow-take, using the same context map as service tasks / gateways.

`validation` is an optional **NCalc** expression (any variable, required or not).
After the required-presence check and after defaults are resolved, each rule is
evaluated against the final collected values overlaid with `sys.*` / `config.*`
context, reusing `SequenceFlowConditionEvaluator.Evaluate` (the gateway
evaluator). A falsy or unresolvable expression rejects the start / flow-take with
a `WorkflowDomainException` (`Variable '<name>' failed validation: '<expr>'`).
Because it shares the gateway grammar, comparisons, boolean/arithmetic operators,
bare-variable truthiness, bracketed context names (e.g. `[sys.user]`), and the
custom helper functions (`Len`, `IsNullOrEmpty`, `Contains`, `StartsWith`,
`EndsWith`, `Lower`, `Upper`, `Trim`, `IsMatch`, etc.) all work -- so a rule like
`Len(name) <= 50` or `IsMatch(email, '^.+@.+$')` is valid. Expressions are
parse-checked at author time in `ValidateDefinition`
(`ValidateVariables` -> `SequenceFlowConditionEvaluator.IsValid`).

### Process variables
In addition to start-event and sequence-flow variables, a workflow may declare
**process-level variables** at the top level (`model.variables`). These are
instance-scoped: never supplied by a user, initialized from their `defaultValue`
at instance start, and mutated by `scriptTask` nodes during pass-through routing.
They are visible to gateways, service-task templates, and validation rules
through the same `WithContext` overlay as any other stored variable.

Process variables differ from start/flow variables in a few respects:

- `required` is meaningless (the value is computed, not collected) and is hidden
  in the editor. `ValidateDefinition` does not reject it but the engine ignores
  it; the editor forces `required: false`.
- `defaultValue` is **required** (validated at author time). Every process
  variable is initialized from its default at start (templated + coerced like a
  start-variable default, so `${sys.user}` / `${amount}` placeholders work), so
  every declared name is readable from hop 0 of the pass-through loop.
- `validation` runs both after the start initialization and after every
  `scriptTask` write that targets the variable, reusing the gateway evaluator.
- The editor surfaces them in the "Workflow" inspector panel (shown when nothing
  is selected), separate from the per-node / per-flow variable editors.

### ServiceTaskConfig
The REST configuration on a `serviceTask` flow node (`flowNode.service`).

```jsonc
{
  "method": "POST",            // GET | POST | PUT | PATCH | DELETE
  "url": "https://api.example.com/credit/${customerId}", // ${var} templated
  "headers": [                 // ${var} templated values
    { "name": "Authorization", "value": "Bearer ${apiToken}" }
  ],
  "body": "{ \"amount\": ${amount} }", // JSON template; ${var} -> variable's JSON value
  "timeoutSeconds": 30,        // per-call timeout (no retries)
  "statusVariable": "creditStatus", // optional; receives the HTTP status (0 on transport error)
  "outputMappings": [          // response field -> instance variable (applied on 2xx)
    { "variable": "creditScore", "path": "score", "required": true },
    { "variable": "approved", "path": "decision.approved" }
  ]
}
```

Each `outputMappings` entry is `{ variable, path, required }`. When `required` is
true and the `path` cannot be resolved from the 2xx response body (or the body is
not valid JSON), the task is failed: the engine routes out an attached
`errorBoundaryEvent`'s error flow if present, otherwise the transition fails with
a `WorkflowDomainException` (rollback + 400) - the same path as a non-2xx/timeout
failure. A non-required miss is silently skipped (the historical behavior). The
`statusVariable` is still written before the failure so an error path can branch
on the HTTP status.

A `serviceTask` may also have an attached `errorBoundaryEvent` (see Error
events); on a non-2xx/timeout/network failure the token routes out the
boundary's error flow instead of failing the transition. The legacy `onError`
field has been removed (old values load tolerantly and are ignored).

In `url` and header values a `${var}` placeholder becomes the variable's scalar
text. In `body` substitution is quote-aware: a placeholder inside a JSON string
literal becomes the variable's escaped scalar text with no added quotes (so
`"message": "Hi ${sys.user}"` -> `"message": "Hi alice"`), while a placeholder in
a bare value position becomes the variable's JSON representation (so
`"amount": ${amount}` stays an unquoted number). A missing variable is an empty
string inside a string and `null` in a bare position. Output `path` is dotted
(`a.b.c`), with numeric segments indexing into arrays (`items.0.id`).

### MessageCatchConfig
The delivery configuration on an `intermediateMessageCatchEvent` or
`messageStartEvent` flow node (`flowNode.message`). All scalar fields are
`${var}`-templatable (`ServiceTaskTemplating.SubstituteScalar`) against instance
variables + context, so a secret can be sourced from `${config.*}` /
`${setting.*}` to stay out of the versioned definition JSON.

```jsonc
{
  "clientId": "svc-orders",            // expected X-Client-Id; ${var} templatable
  "clientSecret": "${config.orderSecret}", // expected X-Client-Secret; ${var} templatable
  "headerName": "X-Webhook-Token",     // required custom header name; ${var} templatable
  "headerValue": "${config.webhookToken}", // required custom header value; ${var} templatable
  "headerValidation": "Len(header) >= 16", // optional NCalc; incoming value bound as `header`; must be truthy
  "idempotencyVariable": "orderId",    // messageStartEvent only: declared start variable used as dedupe key
  "outputMappings": [                  // extract from the inbound JSON message body (raw/uncoerced)
    { "variable": "approved", "path": "decision.approved", "required": true },
    { "variable": "reference", "path": "ref" }
  ]
}
```

Each `outputMappings` entry is `{ variable, path, required }` (same shape as a
`serviceTask`'s response mappings). When `required` is true and the `path`
cannot be resolved from the message body (or the body is missing/not valid
JSON), the delivery/start is rejected with a `WorkflowDomainException` (400)
before any variables are written, so a partial delivery does not persist. A
non-required miss is silently skipped.

`idempotencyVariable` is a `messageStartEvent`-only optional field (ignored by
an intermediate catch). When set it must name one of the node's declared start
variables; the engine looks for the dedupe key value in the request headers
(`Idempotency-Key` or `X-Idempotency-Key`) and maps it to this variable (see
the message-start idempotency behavior below).

**Intermediate catch delivery** (`POST /api/instances/{id}/message`,
`AllowAnonymous`): the engine resolves the templated
`clientId`/`clientSecret`/`headerName`/`headerValue` against the instance
variables overlaid with `config.*`/`setting.*` and the `sys.*` entries an
unverified caller cannot influence (`sys.now`, `sys.today`, `sys.instanceId`,
`sys.workflowId`, `sys.nodeId`, `sys.nodeName`) - `sys.user` and `sys.roles`
are excluded so a caller cannot satisfy a credential templated from `${sys.user}`;
requires the incoming `X-Client-Id`/`X-Client-Secret` to equal the resolved
client credentials (constant-time compare); requires the resolved header to be
present, equal the resolved `headerValue` (constant-time compare), and (when
`headerValidation` is set) satisfy the NCalc rule with the incoming value bound
as `header` against the full context (caller `sys.user` included, since the
caller is by then authenticated); then applies `outputMappings` from the raw
JSON message body (dotted-path `ServiceTaskTemplating.TryExtract`, written raw
via `AddVariableAsync` - targets need not be declared, mirroring a
`serviceTask`); a `required` mapping whose path is unresolvable (or a
missing/invalid JSON body) rejects the delivery with a
`WorkflowDomainException` (400) before any variables are written. A client
id/secret mismatch throws `WorkflowUnauthorizedException` (401); a header problem
(missing/mismatch/validation failure), a required-mapping failure, or a
not-running / not-waiting instance throws `WorkflowDomainException` (400). The
resolved client id is recorded as `performedBy` / `sys.user` for attribution.
The endpoint returns a slim `MessageDeliveryAckDto` (`Id`, `CurrentNodeId`,
`CurrentNodeName`, `CurrentNodeExternalId`, `Status`, `UpdatedAt`) rather than
the full `InstanceDetailDto`, so a node-credentialed webhook caller cannot read
the workflow definition (which may contain other nodes' literal secrets) or the
instance's stored variables/history. There is no delivery idempotency key; a
retry after a successful delivery gets a `400` because the instance has already
advanced off the catch node.

**Message-start delivery** (`POST /api/workflows/{workflowKey}/message-start`,
`AllowAnonymous`): same auth mechanics, but the credential/header templates are
resolved against an instance-less context (`config.*`/`setting.*` +
non-caller-influenced `sys.*`; no `sys.instanceId`/`sys.user`/`sys.roles` since
there is no caller/instance yet). `outputMappings` feed the start-variable values
(passed into the same validation/resolution pipeline as a user `POST /api/instances`).
When `idempotencyVariable` is set, before creating an instance the engine acquires
a transaction-scoped `pg_advisory_xact_lock(workflowKey, hashtext(keyValue))`,
searches for an existing instance of the workflowKey already carrying that key
value (via the variable-search path), and returns that instance's ack instead of
creating a duplicate. The key must be provided in the `Idempotency-Key` or `X-Idempotency-Key`
request header (else 400).
A slim `MessageStartAckDto` (`InstanceId`, `CurrentNodeId`, `CurrentNodeName`,
`CurrentNodeExternalId`, `Status`, `CreatedAt`) is returned (never the full
definition/variables/history, since the endpoint is `AllowAnonymous`).

### Context sources (`sys.*` / `config.*` / `setting.*`)

Beyond stored instance variables, service-task templates, exclusive-gateway
conditions, and variable `defaultValue` templates / `validation` rules can read
**read-only context** resolved at evaluation time and **never persisted** to
`instance_variables`. During pass-through routing (`ResolvePassThroughAsync`) and
at start / flow-take (variable default resolution and validation), `WithContext`
overlays a context map (built by `BuildContextMap` from the `ActorContext`, the
instance, the definition, the current node, an injected `TimeProvider`,
`WorkflowContextOptions`, and the `workflow_settings` table) onto a copy of the
stored variables; context wins on any name collision. Available keys:

- `sys.now` (UTC ISO-8601), `sys.today` (`yyyy-MM-dd`)
- `sys.user`, `sys.roles` (array)
- `sys.instanceId`, `sys.workflowId`, `sys.workflowName`, `sys.nodeId`, `sys.nodeName`
- `sys.claim.<name>` for each allowlisted JWT claim (matched by exact type or the
  last segment of a URI-style claim type)
- `config.<name>` for each server-side config entry (keeps secrets out of the
  versioned definition JSON)
- `setting.<name>` for each row in the `workflow_settings` database table
  (global, read-only, manually inserted; loaded once per request and cached).
  When a row has a non-null `Namespace`, the key becomes
  `setting.<namespace>.<name>` (e.g. `setting.finance.taxRate`).

In service-task `url`/`headers`/`body` these are used like any placeholder, e.g.
`${sys.user}`, `${config.apiToken}`, `${setting.taxRate}`. In NCalc gateway
conditions the dotted names need bracket syntax, e.g. `[sys.user] == requester`
or `[sys.now] > deadline` or `[setting.taxRate] > 0`. Configuration binds from
the `WorkflowContext` section (`WorkflowContext:Config:<name>` and
`WorkflowContext:AllowedClaims`). User variable names starting with `sys.` /
`config.` / `setting.` are rejected so context can never be shadowed or spoofed.

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
| `type: "task"` | Abstract/automatic Task | Pass-through activity completed with no user action; closest to a BPMN Task without an implementation. |
| `type: "serviceTask"` | Service Task | Automatic REST call (SVC marker); templated request from variables, response mapped back into variables. Simplified: REST only, synchronous, no retries. |
| `type: "scriptTask"` | Script Task | Automatic variable mutation (SCRIPT marker); either NCalc assignments or a Jint-run JavaScript body (`scriptFormat`) writes process variables during the pass-through hop. Simplified: both run in-process (Jint, sandboxed, no CLR) rather than spawning an external script engine/process. |
| `type: "exclusiveGateway"` | Exclusive Gateway (XOR) | Diamond; routes to the first outgoing flow whose condition matches, else the default flow. |
| `type: "endEvent"` | None End Event | Terminal marker; thick-ring circle. |
| `type: "errorEndEvent"` | Error End Event | Terminal marker; thick-ring circle with error glyph. Ends the instance with `Faulted` status. Simplified: no error code (catch-all); no subprocess, so an error end event is reached via a boundary's error path rather than by throwing out of a subprocess. |
| `type: "errorBoundaryEvent"` | Error Boundary Event (interrupting) | Attached to a `serviceTask`/`scriptTask`; catches the host's runtime failures and routes out the boundary's single error flow. Simplified: interrupting only; catch-all (no error code match); at most one per host; no other boundary trigger types (timer/message/signal) yet. |
| `type: "intermediateMessageCatchEvent"` | Intermediate Message Catch Event | A resting node that waits for a message delivered via `POST /api/instances/{id}/message`; thin double-ring circle with an envelope glyph. Auth is the node-config client id/secret + a required custom header (with optional NCalc validation), not the user JWT. Simplified: correlation by instance id only (no cross-instance message-name/signal matching); no timeout escape hatch (a future timer boundary could address). |
| `type: "messageStartEvent"` | Message Start Event | An entry point started by an external system via `POST /api/workflows/{workflowKey}/message-start`; thin single-ring circle with an envelope glyph. Carries start `variables` + `message` config (same as an intermediate catch, plus an optional `idempotencyVariable`). System-only (`IsStart` is false). The engine creates the instance and auto-advances off it (pass-through, history note `messageStart`). Simplified: instance-less credential resolution (no `sys.user`/`sys.roles`/`sys.instanceId` for credentials since there is no caller/instance yet); idempotency via an advisory-locked variable-search dedupe (no new table). |
| `sequenceFlow` | Sequence Flow | First-class directed edge with its own id, `sourceRef`, `targetRef`. |
| `sequenceFlow.condition` | Condition Expression | NCalc expression on user-task and gateway flows (comparisons, boolean/arithmetic operators, functions, bare-variable truthiness). |
| `sequenceFlow.isDefault` | Default Flow | The gateway's fallback path; on a user-task flow it means the action is always visible regardless of condition. |
| `lane` | Lane (within a Pool) | Swimlane-style container; assignment is geometric, not a formal participant/pool model. |
| `roles` | Lane / Performer (Potential Owner) | Free-text candidate roles; not a formal resource/assignment model. |
| `variables` (start/flow) | Data Object / Property | Typed data captured at a start event or on a user-task flow. |
| `variables` (process) | Data Object (process-scoped) | Instance-scoped typed data, initialized at start and mutated by `scriptTask` nodes; closest to a BPMN Data Object owned by the process. |
| `assignment` | Script Task expression / Data Write | NCalc expression whose typed result is coerced to the target variable's `dataType` and persisted. |
| `requiresClaim` | User Task "claim" (assignee) | Runtime-enforced single-owner locking; not a distinct BPMN element. |

### Intentional deviations from BPMN

- **Only exclusive gateways.** No parallel/inclusive/event-based gateways yet;
  branching is either a `userTask` with multiple named flows or an
  `exclusiveGateway` routed by conditions.
- **Service tasks are REST only.** A `serviceTask` invokes an HTTP/REST endpoint
  synchronously during the pass-through hop with a bounded timeout and no
  retries, incidents, or async job execution; other BPMN implementations
  (connectors, expressions, message/send-receive) are out of scope.
- **Error events, one message catch event, and one message start event (no timer/signal yet).** Plain
  (none) start and end events plus `errorEndEvent` and `errorBoundaryEvent`
  (catch-all, no error codes), `intermediateMessageCatchEvent` (correlation
  by instance id only, no cross-instance signal/message matching, no timeout),
  and `messageStartEvent` (instance-less system-only entry with advisory-locked
  variable-search idempotency); no timer/signal events yet.
- **No pools / collaboration.** Lanes exist without a multi-party pool or message
  flow.
- **NCalc condition language.** Gateway conditions are evaluated with NCalc, so
  comparisons (`== != < <= > >=`), boolean operators (`and`/`or`), arithmetic,
  parentheses, functions, and quoted string literals are all supported (a bare
  variable name is still a truthiness check). DMN-style decision tables remain
  out of scope.
- **Integer ids, JSON (not BPMN XML).** Flow nodes and sequence flows use integer
  ids so runtime tables stay integer-keyed; the definition is JSON, not BPMN XML.
- **Node roles are enforced** at runtime (against JWT role claims); an empty
  `roles` list means open to anyone. Sequence-flow `roles` remain advisory, but
  `userTask` flows can also carry a `condition` (NCalc) and `isDefault` flag that
  the engine evaluates at both list and execution time. `requiresClaim` ownership
  is enforced on top of the role check.

### If you add BPMN-aligned features later

- Prefer BPMN names for new node types/elements (e.g. `serviceTask`,
  `parallelGateway`, `timerStartEvent`) and add matching shapes.
- Keep the JSON tolerant: extend `loadFromObject()` (editor) and
  `WorkflowModelMigrator` (.NET) so older documents still load.
- Update `BpmnFlowNodeTypes` predicates (`IsStart`, `IsEnd`, `IsErrorEnd`,
  `IsErrorBoundary`, `IsAutomatic`, `IsServiceTask`, `IsScriptTask`,
  `IsUserTask`, `IsGateway`, `IsMessageCatch`, `IsPassThrough`) and
  `ValidateDefinition` together so the engine and editor agree.

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
  the pass-through loop with `automatic` / `start` history notes. `serviceTask`
  and `scriptTask` are also pass-through (single unconditional outgoing flow),
  logged with `service` / `script` notes.
- **Message catch flow**: an `intermediateMessageCatchEvent` owns one
  unconditional outgoing flow, drawn as a solid (non-dashed) edge since the node
  rests (it is not pass-through). The engine advances down it on message
  delivery, logged with a `message` history note.
- **Gateway flows**: `exclusiveGateway` outgoing flows carry a `condition` or the
  `isDefault` marker; the editor shows the condition/`default` beneath the edge
  and enforces a single default per gateway.
- **User-task flows**: `userTask` outgoing flows may also carry a `condition` (NCalc)
  and an `isDefault` marker; the editor shows both roles and condition/default beneath
  the edge and enforces a single default per user task.
- **User-task node condition**: a `userTask` may itself carry a `condition` (NCalc)
  that acts as a visibility gate. When false, the task is hidden from the inbox,
  `GetAvailableFlowsAsync` returns no flows, and `ClaimAsync`/`TakeFlowAsync` reject.
  The instance still rests on the node; routing is unchanged. The editor shows a
  `COND` marker on the node and a `Condition` field in the inspector.

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
