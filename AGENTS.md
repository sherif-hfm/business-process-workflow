# Flowbit — Workflow Editor

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
| `flowbit-editor.html` | The entire application: markup, CSS, and JS in one file. |
| `workflow.json` | A sample/exported workflow ("Purchase Request Approval") used as example data. It matches the JSON format the editor saves and loads. |
| `Flowbit/` | .NET 10 Web API + Blazor Server workflow runtime using PostgreSQL. |
| `AGENTS.md` | This document. |

To run it, open `flowbit-editor.html` directly in a modern browser. No install,
no server required.

---

## How it works (architecture)

Everything lives in `flowbit-editor.html`. The key pieces:

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

The editor is dependency-free at runtime. Its save validator and selected editor
helpers are covered by the `Flowbit/tests/Flowbit.Tests` test project; visual and
pointer interactions still require manual in-browser validation.

---

## Runtime engine (`Flowbit/`)

`Flowbit/` is a separate .NET 10 solution for running workflow instances
from the JSON definitions produced by the editor. It preserves the editor's JSON
format rather than normalizing the definition into node/flow tables.

Projects:

- `src/Flowbit.Api` - API layer: Minimal API endpoints, OpenAPI, startup
  composition, development migration/seed.
- `src/Flowbit.Service` - Service layer: workflow engine behavior,
  definition validation, sequence-flow condition evaluation
  (`SequenceFlowConditionEvaluator`, NCalc), service interfaces, repository
  ports, and DI extension.
- `src/Flowbit.Infrastructure` - Infrastructure layer: EF Core,
  PostgreSQL/Npgsql, JSONB mapping, migrations, repository implementations,
  scriptTask JavaScript execution (`JintScriptEvaluator`, sandboxed Jint), and
  unit of work.
- `src/Flowbit.Shared` - shared DTOs and C# model for the editor JSON.
- `src/Flowbit.Ui` - Blazor Server UI that calls the API through a typed
  `HttpClient`.

Storage follows the hybrid design:

- All Flowbit tables, owned sequences, indexes, constraints, and EF migration
  history live in the fixed PostgreSQL `flowbit` schema. Table names below are
  shown without that schema qualifier for readability.

- Workflow definitions are immutable/versioned JSONB snapshots in
  `workflow_definitions`. Each row also carries `WorkflowKey`, an indexed integer
  stamped from the editor JSON model `id` on every save; it is the same across all
  versions of a workflow, so it acts as a stable cross-version key for instance
  search (see the workflow key search below).
- Runtime state is normalized in `workflow_instances`, `execution_tokens`,
  `user_tasks`, `instance_variables`, `instance_history`,
  `sequence_flow_occurrences`, `sequence_flow_summaries`, and
  `message_delivery_receipts`. An instance row
  owns lifecycle status and timestamps but no longer stores a single current
  step or claim. `execution_tokens` own execution position and its node snapshot;
  `user_tasks` are work items created when a token rests on a `userTask` and own
  roles, claim requirements, claimant, and task lifecycle timestamps. The current
  engine preserves one active execution token per instance; a multi-instance user
  task can own many active or pending work items beneath that parent token.
- `workflow_instances` snapshots the stable `WorkflowKey` and private nullable
  `IdempotencyKey` for keyed starts. Permanent ownership lives in
  `workflow_idempotency_claims`, keyed by `(WorkflowKey, IdempotencyKey)` with
  deterministic `C` collation and a unique, restrictive link to its owning
  instance. Business-key ownership remains separate in
  `workflow_business_key_claims`; transport idempotency and domain identity are
  deliberately independent contracts.
- **Token/task projections.** Instance list node filters and current-node display
  are projected from the latest execution token. Inbox reads query active
  `user_tasks` directly and apply claim/role predicates there; they do not infer
  work from `workflow_instances`. Inbox ordering defaults to task
  `UpdatedAt DESC, Id DESC`, but callers may select the documented task or
  instance fields and direction. Sorting remains in PostgreSQL and is applied
  after one-per-actor representative selection, before paging and enrichment.
  Database membership, count, ordering, and paging remain authoritative. The
  returned page batch-loads latest instance variables and multi-instance state;
  flow roles, claim-bypass roles, and stored-state conditions then refine only
  action visibility and `CanAct`/`CanClaim`. A task with no available action
  stays in its SQL page disabled, and no per-task database reads are performed.
  Entering a user task creates an active work item, leaving completes it, and
  cancellation cancels it. Claim/unclaim updates the work item. The migration
  backfills one token and (where applicable) one active user task for existing
  instances before dropping the old `CurrentStepId`, `CurrentNode*`,
  `CurrentRequiresClaim`, and `ClaimedBy` instance columns.
  Summary/detail DTOs expose a grouped `UserTasks` work summary (`IsMultiInstance`,
  active/pending/claimed/assigned counts, and sole claimant/assignee). Top-level
  instance DTOs do not expose `ClaimedBy`; claim ownership belongs to task DTOs.
  Progress and work-summary projections use bounded grouped queries rather than
  loading every child item.
- **Multi-instance user tasks.** A `userTask.multiInstance` configuration creates
  parallel or sequential work items while retaining one parent execution token.
  `collection` mode snapshots a declared `string[]` and directly assigns each
  username; `cardinality` mode evaluates an NCalc count and uses the normal
  role/claim pool. An opt-in `onePerActor=true` cardinality execution presents one
  representative inbox item per actor and permits that username to complete at
  most one child item in the execution (case-insensitive); concurrent actors may
  see the same representative, so the first completion wins and a stale action
  returns 409 before the actor refreshes to another item. Fan-out is bounded by `Workflow.MultiInstance.MaxInstances`
  (default 1000). Each item records its selected flow and local submitted values.
  An outcome flow with `isSelectable=false` is engine-only: it remains in the
  diagram and may win aggregate routing, but is omitted from available actions
  and rejected by user action endpoints. Engine-only flows are supported only on
  multi-instance user tasks. Every multi-instance task has exactly one pure
  engine-only default fallback (`isDefault=true`, `isSelectable=false`) with no
  condition or priority; normal user tasks do not support default flows. Older
  selectable multi-instance defaults are normalized into the original selectable
  outcome plus a synthesized hidden fallback to the same target.
  Outcome-flow `completionCondition` expressions use `CountFlow(flowId)`,
  `PercentFlow(flowId)`, and `mi.total/completed/remaining`; the lowest
  `completionPriority` wins. `multiInstance.completionEvaluation` controls when
  these aggregate conditions run: `afterEach` (the default for missing/older
  definitions) evaluates after every completed item and permits an early quorum,
  while `afterAll` evaluates only after every item completes. Interrupting flows
  remain immediate in both modes. Only non-default outcomes participate in
  condition/priority evaluation. A winning condition or interrupt atomically
  cancels unfinished items, writes the ordered JSON result collection, and advances
  the parent token once. Each child result is tagged with `kind: "item"` and snapshots
  the completing actor's normalized JWT roles in `userRoles`; cancelled/non-action
  rows use `null`, while an actor with no roles uses `[]`. A direct parent-level
  interrupt appends one final `kind: "parentInterrupt"` result carrying the interrupt
  flow, actor, roles, timestamp, and submitted values after the index-ordered child
  rows. If all items finish without a match, the required default
  outcome wins. Task-specific operations use `/api/user-tasks/{taskId}`; legacy
  instance-addressed actions return 409 when multiple active tasks are ambiguous.
  An authorized actor can also discover and take parent-level interrupt flows through
  `/api/multi-instance-executions/{executionId}/flows`; these actions require both
  the node and flow roles but deliberately do not require an active assigned child
  item or claim. The execution row is locked so concurrent interrupts advance the
  parent exactly once, while later attempts return 409.
- Multi-instance definition enum values (`mode`, `source`, and
  `completionEvaluation`) canonicalize recognized casing but reject null,
  unknown, or misspelled values. Definition validation rejects duplicate node/
  flow ids and case-insensitive duplicate variable names before any singular
  lookup. Cardinality is range/integrality checked before casting, and collection
  enumeration stops immediately above `Workflow.MultiInstance.MaxInstances`;
  collection usernames share the 300-character assignee limit.
- **Instance-wide flow evidence (`FlowInfo`).** Definitions may inspect a
  sequence flow's lifetime summary with NCalc `FlowInfo(flowId, 'path')`; both
  arguments must be literals and the flow id must exist. Canonical paths are
  `all`; `actions.count`; `actions.last.user`, `userRoles`, `occurredAt`, `kind`,
  or `values`; and the equivalent `traversals.*` paths. `all` and JavaScript
  `execution.getFlowInfo(flowId)` return
  `{ flowId, actions: { count, last }, traversals: { count, last } }`; each
  non-null `last` contains `user`, `userRoles`, `occurredAt`, `kind`, and
  flow-local `values`. Known unused flows return zero/null summaries.
  `actions` are explicit actor selections, while `traversals` are token movement:
  a normal user-task action and parent MI interrupt are both; an MI child vote is
  action-only; the aggregate outcome/default is traversal-only; and automatic
  routing is traversal-only. Evidence snapshots the validated actor and all roles
  at action time, and staged evidence is visible to a downstream gateway/script
  in the same transaction. `FlowInfo` is allowed only in exclusive-gateway and
  MI completion conditions, NCalc script assignments, and JavaScript scripts;
  user-action visibility, assignee/cardinality, and variable/output/header
  validation expressions reject it. Existing `CountFlow`/`PercentFlow` semantics
  remain current-MI-execution scoped.
  For definitions using the feature, each event appends an audit occurrence and
  upserts a fixed-size instance/flow summary in the locked transaction. Runtime
  evaluation loads summaries once rather than scanning detailed history, so its
  read cost does not grow with loops or MI fan-out. Definitions without a
  `FlowInfo` reference skip the summary query and write no evidence rows. The
  additive migration has no historical backfill: existing instances expose only
  post-deployment evidence.
- Instance transitions run in a database transaction and lock the instance row
  with `SELECT ... FOR UPDATE`; there is no in-memory run engine state. Mutations
  use a consistent instance -> multi-instance execution -> user-task lock order.
  Instance cancellation locks the instance before discovering/cancelling active
  executions and open tasks, and child activity touches the parent `UpdatedAt` in
  the same transaction.
- **Pass-through routing** (`ResolvePassThroughAsync`): `startEvent`,
  `messageStartEvent`, automatic `task`, `serviceTask`, `scriptTask`,
  `exclusiveGateway`, and `errorBoundaryEvent` nodes are resolved in the same
  transaction until the instance rests on a `userTask` or
  `intermediateMessageCatchEvent`, or terminates on an `endEvent`/`errorEndEvent`.
  A hop limit (`flowNodes.Count + 1`) guards against cycles. History rows are
  written with a `start`, `messageStart`, `automatic`, `service`, `script`,
  `gateway`, `boundary`, `error`, or `message` note. Gateway history also stores
  the selected sequence-flow id; automatic gateway rows are excluded from
  previous-actor claim inheritance even though that audit id is populated.
- **Service tasks** select a connector through `service.type`; `rest` is the only
  connector implemented today, while the explicit discriminator keeps the model
  and editor dropdown extensible. REST calls run during the pass-through hop
  (`IServiceTaskInvoker` / `HttpServiceTaskInvoker`, a typed `HttpClient`). The
  URL, header values, and JSON body are built by substituting `${var}`
  placeholders from instance variables (`ServiceTaskTemplating`); on a `2xx`
  response the configured typed `outputMappings` extract dotted-path values,
  apply ordered defaults, strictly validate their declared JSON types and NCalc
  rules, then write the complete batch to instance variables (latest write wins).
  A target matching a process variable must use its type/array contract and runs
  both validations; undeclared targets are created by the mapping. No output is
  written until every mapping succeeds. The call is synchronous inside
  the locked transaction with a bounded `timeoutSeconds` and **no retries**. The
  deployment-wide `WorkflowServiceTasks.MaxTimeoutSeconds` caps authored timeouts,
  `MaxResponseBodyBytes` bounds buffered responses, and the typed client's global
  timeout is disabled so the node timeout is authoritative. Missing URL/header
  placeholders fail strictly; missing body placeholders retain the permissive
  empty-string/`null` behavior. On a
  non-2xx/timeout/network failure the engine looks up an attached
  `errorBoundaryEvent` (see Error events). If one exists the token routes out the
  boundary's single error flow (and an optional `statusVariable` still receives
  the HTTP status, 0 before a response exists, so the error path can branch); if none
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
     `execution.hasVariable(name)`, `execution.getFlowInfo(flowId)`. JavaScript
     definitions explicitly opt into the last capability with `usesFlowInfo=true`;
     older definitions with a direct `execution.getFlowInfo(...)` call are inferred
     during compatibility normalization. A script reads its own writes within the
     same execution (an in-memory overlay, same as the ncalc path). Dynamic
     `eval`/`Function` compilation is disabled and both author-time parsing and
     runtime execution use strict mode. `ScriptOptions` (`WorkflowScript`) bounds
     elapsed time, statements, memory, recursion, execution stack, regex duration,
     array size, and the depth/item/UTF-8 size of values crossing the JavaScript/JSON
     bridge. Caller cancellation is propagated instead of being converted into a
     task failure. These in-process Jint constraints are cooperative safeguards for
     trusted workflow administrators, not an out-of-process hostile-code boundary.
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
- **Exclusive gateways** evaluate non-default outgoing flows in ascending
  positive `conditionPriority`; the first flow whose `condition` is true wins,
  otherwise the required `isDefault` flow is taken. New and updated definitions
  require exactly one default, a nonblank condition and unique positive priority
  on every other outgoing flow, and at least two outgoing flows. Older immutable
  definitions with no authored priorities derive `1..n` from their existing
  `sequenceFlows` array order; older definitions without a default remain
  executable and fail at runtime only when no condition matches. Conditions are evaluated by
  `SequenceFlowConditionEvaluator` using [NCalc](https://github.com/ncalc/ncalc):
  instance variables are exposed as parameters and the full NCalc grammar is
  supported (comparisons `== != < <= > >=`, boolean `and`/`or`, arithmetic,
  parentheses, quoted string literals). String comparisons are case-insensitive;
  the result is coerced to a boolean so a bare variable name still works as a
  truthiness check (finite non-zero numbers / nonblank strings are truthy;
  non-finite numeric results are false). Large finite numbers do not overflow
  truthiness conversion. An optional
  `${ ... }` wrapper is stripped, and invalid or unresolvable expressions
  evaluate to `false`. Conditions are also parse-checked when a definition is
  created/updated. Beyond NCalc's built-ins (`Min`, `Max`, `if`, `in`, math
  helpers), `SequenceFlowConditionEvaluator` registers **custom helper functions**
  (case-insensitive names, wired once in `CreateExpression` so evaluation and
  parse-checking agree): `Length(s)` / `Len(s)`, `IsNullOrEmpty(s)`,
  `IsNullOrWhiteSpace(s)`, `Contains(s, sub)`, `StartsWith(s, prefix)`,
  `EndsWith(s, suffix)`, `Lower(s)`, `Upper(s)`, `Trim(s)`, and
  `IsMatch(s, pattern)` (regex match, case-insensitive and culture-invariant,
  with bounded execution time as a ReDoS guard). Substring/regex matching is
  case-insensitive. These helpers work
  anywhere NCalc runs (gateway/flow conditions, assignee expressions, and
  variable `validation` rules). A
  helper called with too few arguments (or a mistyped name) is treated as unknown;
  since author-time `IsValid` only parse-checks grammar, such typos are not flagged
  and the expression simply evaluates to `false` at runtime.
- `requiresClaim` **is** enforced at runtime: such a `userTask` must be claimed
  (`POST /claim`) before its flows are available or can be taken, only the
  claiming user may act, and the claim is released on transition. `unclaim`
  clears it. Claim ownership is tracked on `user_tasks.ClaimedBy`
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
  default. A boundary is pass-through (it auto-advances its one unconditional
  error flow, history note `error` then `boundary`) and may have no incoming
  flows; at most one boundary per host. An `errorEndEvent` must have at least
  one incoming flow and no outgoing flows. Newly authored definitions give it a
  required stable `errorCode` (case-sensitive, up to 300 characters, matching
  `[A-Za-z0-9][A-Za-z0-9._-]*`) and an optional `errorDescription` (up to 1000
  characters, falling back to the node name). Entering it ends the instance with
  `Faulted`; the code and resolved description are snapshotted on the execution
  token and exposed as `fault` by instance and transition DTOs. Legacy faulted
  tokens keep a nullable code and use the terminal node name as their description.
  It is typically reached via a boundary's error path, directly or through a
  handler task. The legacy
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
  Typed `outputMappings` extract dotted-path values from the inbound JSON,
  resolve ordered defaults, enforce strict scalar/array types, and evaluate NCalc
  rules against the final overlay. A required mapping must resolve from the
  current delivery or its mapping default; an older stored value cannot satisfy
  it. All mappings are staged before any write, so a failure returns 400 and
  leaves the instance waiting with no partial output. The
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
  **Idempotency.** A catch may opt in with `message.deliveryIdempotency=true` and
  select its transport header with `message.deliveryIdempotencyHeaderName`
  (default `Idempotency-Key`). The `X-Idempotency-Key` alias is accepted only
  when the configured name is `Idempotency-Key`; custom names have no alias.
  The endpoint permanently reserves the trimmed,
  exact key for that instance in `message_delivery_receipts`. The first committed
  delivery returns 200; every authenticated reuse returns 409 and cannot consume a
  later catch. A receipt also owns the exact catch wait-history occurrence, so
  concurrent requests for one wait have one winner. Failed/rolled-back deliveries
  reserve nothing. Unconfigured catches retain the legacy behavior.
  Non-empty bodies must use a JSON media type, malformed JSON returns 400, and
  `WorkflowMessageDelivery.MaxPayloadBytes` bounds the request (default 1 MiB).
  No timeout escape hatch exists yet (a waiting instance waits indefinitely, like
  an unclaimed `userTask`); a future timer boundary event could address that.
- **Message start events.** A `messageStartEvent` is an entry point (like a
  `startEvent`) that is started by an external system via
  `POST /api/workflows/{workflowKey}/message-start` rather than by a user. It is
  **system-only**: `IsStart` is intentionally false, so the user `POST /api/instances`
  path rejects it and the Blazor "pick a start event" page does not list it. It
  carries a `message` config whose typed `outputMappings` are its start-variable
  declarations (there is no separate node `variables` section). The remaining
  config is shared with an intermediate catch:
  `clientId`/`clientSecret`/`headerName`/`headerValue`/`headerValidation`/
  `outputMappings`. The caller addresses
  the workflow by its stable cross-version `workflowKey` (the published default
  version is resolved); an optional, single-valued `?startEvent={externalId}`
  selects a specific message-start event when the workflow has more than one.
  Multiple message starts must have nonblank, case-sensitive unique external ids;
  missing, repeated, ambiguous, or unknown selectors return 400. Auth mirrors the
  intermediate catch: exactly one client id/secret via
  `X-Client-Id`/`X-Client-Secret` (401 on mismatch) + required header (400 on
  missing/mismatch/validation failure), all `${var}`-templatable against an
  instance-less context (`config.*`/`setting.*` + non-caller-influenced `sys.*`;
  no `sys.user`/`sys.roles`/`sys.instanceId` since there is no caller/instance
  yet). Credential and correlation templates are resolved from fresh settings and
  fail closed when a required placeholder is missing. Each mapping declares
  `variable`, optional `path`, `dataType`, `isArray`,
  `required`, optional `defaultValue`, and optional NCalc `validation`. Supplied
  values are strictly typed; a missing path uses its default before the final
  required check. It is pass-through: after
  creating the instance the engine auto-advances off it (history note
  `messageStart`), like a `startEvent`. Entry-level transport idempotency uses the
  generic `flowNode.idempotency` contract described below; it is no longer part
  of `message`.
  Non-empty request bodies must use a JSON media type, malformed JSON returns 400,
  and `WorkflowMessageDelivery.MaxPayloadBytes` bounds the request (default 1 MiB).
  A slim `MessageStartAckDto` (`InstanceId`, `CurrentNodeId`, `CurrentNodeName`,
  `CurrentNodeExternalId`, `Status`, `CreatedAt`, and an optional `Fault`) is
  returned (never the full
  definition/variables/history, since the endpoint is `AllowAnonymous`). Its
  node type fits the 32-character execution-token `NodeType` column.
- **Generic start idempotency.** A `startEvent` or `messageStartEvent` may
  optionally configure
  `idempotency: { headerName, variable }`. An unconfigured entry ignores
  idempotency headers and retains unlimited-start behavior. A configured entry
  requires exactly one value in its configured HTTP header; the value is
  trimmed, must be nonblank, is limited to 300 Unicode scalar values, and is
  compared exactly and case-sensitively without Unicode normalization. When
  `headerName` is `Idempotency-Key`, `X-Idempotency-Key` is accepted as an alias;
  if both appear their trimmed values must be equal. Custom names have no alias.
  The key is permanently unique for the stable `WorkflowKey`, spanning all
  versions, configured entries, and both start routes; another workflow family
  may reuse it. `variable` is an implicit required scalar string populated only
  from that header and persisted as an instance variable. It must not collide
  case-insensitively with an entry variable/output mapping or the business-key
  variable. Authentication, framing, message-client, and the configured message
  correlation headers cannot be used as idempotency headers.

  Reservation uses `workflow_idempotency_claims` with the composite primary key
  `(WorkflowKey, IdempotencyKey)` and row locking inside the start transaction.
  Idempotency is reserved before the business key. A committed owner returns
  `409`, `Location: /api/instances/{id}`, and
  `{ "code": "idempotency_conflict", "instanceId": id }` from either start
  route. Completed, faulted, and cancelled owners remain permanent. Any later
  validation, mapping, business-key, script/service, or routing failure rolls
  back the new claim with the instance. External side effects made before a
  later rollback still need their own downstream idempotency contract. Raw keys
  are never returned or logged. Legacy `message.idempotencyVariable` definitions
  normalize in memory to this node-level shape; the migration backfills valid
  historical owners and fails rather than guessing malformed or colliding keys.
- **Generic start business keys.** A `startEvent` or `messageStartEvent` may
  configure `businessKey: { variable, uniqueness }`, where `variable` is an
  exactly named required scalar `string` start variable with no default (a typed
  mapping with a nonblank path on a message start) and
  `uniqueness` is `active` or `all`. Once any entry enables the feature, every
  entry in that definition must configure it. Values are explicit JSON
  strings, trimmed, nonblank, limited to 300 characters, and compared exactly
  and case-sensitively. Claims are scoped by stable `WorkflowKey` across all
  definition versions. `active` releases when the instance becomes completed,
  faulted, or cancelled; `all` remains permanent. Normal duplicate starts return
  409 (`business_key_conflict`, `existingInstanceId`, and `Location`). A
  message-start duplicate also returns 409, but its slim anonymous response uses
  `code: "business_key_conflict"`, `instanceId`, and `Location`. Message
  Entry idempotency may coexist only on a different variable: idempotency identifies a
  transport retry, while the business key identifies the domain instance;
  idempotency is checked first and therefore determines the conflict when both
  keys already exist.
  Enablement is prospective and irreversible for a workflow key: existing
  instances stay unkeyed/non-blocking, and older unkeyed versions cannot start.
- **Node roles are enforced** at runtime for `userTask` and user-initiated
  `startEvent` nodes. The caller's identity and roles come from a validated JWT
  (name + role claims), not from request fields.
  - A `userTask` with a non-empty `roles` list can only be claimed/acted on by a
    caller holding one of those roles; an empty `roles` list is open to anyone.
    `GetAvailableFlowsAsync` hides flows when the role does not match;
    `ClaimAsync`/`TakeFlowAsync` reject with a `WorkflowDomainException`.
  - A user-initiated `startEvent` with a non-empty `roles` list can only be
    started by a caller holding one of those roles; an empty `roles` list is
    open to anyone. `StartInstanceAsync` rejects unauthorized starts with a
    `WorkflowDomainException`.
  Sequence-flow `roles` are enforced at runtime for `userTask` flows (against
  JWT role claims, same semantics as node roles: empty list = open to anyone).
  `GetAvailableFlowsAsync` hides a flow whose roles the actor doesn't hold, and
  `TakeFlowAsync` rejects an attempt with a `WorkflowDomainException`. This
  stacks on top of the task's own node roles and claim ownership. `userTask`
  flows can also carry a `condition` (NCalc): the engine filters visible actions
  using values already stored on the instance and re-checks the same stored-state
  guard under lock before applying submitted action variables. A flow with
  `canActWithoutClaim=true` may additionally declare `canActWithoutClaimRoles`;
  these roles stack with node/flow roles only when the caller does not own the
  claim. Empty bypass roles preserve unrestricted bypass for otherwise-authorized
  actors, including when another actor owns the claim. Direct assignment is never
  bypassed.
- **Direct user-task assignment.** A normal (non-multi-instance) `userTask` may
  carry an `assignee` NCalc expression. The expression is evaluated once when
  the work item is created against the latest instance variables plus the normal
  `sys.*`/configuration context; a valid non-empty string (maximum 300 characters)
  is snapshotted to `user_tasks.Assignee`. Assigned tasks require no claim, but
  node and flow roles still apply. Missing/unresolvable/non-string results log a
  warning and create a shared-pool task using the node's `requiresClaim` setting,
  unless the node also uses `requiresAssignment`, in which case the unassigned
  task remains hidden for distribution.
  Assignment matching is case-insensitive and is enforced by inbox, flow, claim,
  and task action endpoints. Existing collection multi-instance assignment is
  unchanged; assignee expressions are rejected on every multi-instance task.
- **Required assignment and assignment inheritance.** A normal `userTask` may set
  `requiresAssignment=true`. While its active work item has no `Assignee`, regular
  inbox and actor-scoped task reads omit it and claim/action endpoints reject it;
  assignment managers and the external distributor can still list and assign it.
  Unassigning hides it again and does not rerun automatic ownership resolution.
  This mode is separate from claiming and cannot be combined with `requiresClaim`,
  non-fresh `claimMode`, or a multi-instance target. The workflow must configure
  top-level `taskDistribution` credentials.
  `assignmentMode` is `fresh` (default), `previous`, or `fromNode` with
  `inheritAssignmentFromNodeId`. Fresh mode may use the normal `assignee`
  expression. Previous/fromNode mode evaluates once on entry and selects the most
  recent completed source work item (including a multi-instance child); its
  recorded `Assignee` wins, otherwise `CompletedBy` is used. If that one selected
  row has neither, the task remains hidden rather than scanning older rows.
  Successful inheritance writes a `taskAssignment` audit row performed by
  `system` with `authority=assignmentInheritance` and source/candidate metadata.
  Starts and default-version changes are guarded so running required-assignment
  work cannot be stranded without current family distributor credentials.
- **Manager-controlled task assignment.** A workflow definition may declare
  top-level `taskAssignmentRoles`. An authenticated actor holding at least one
  configured role can list every active work item for that workflow through the
  task-management API and directly assign, reassign, or unassign it. This applies
  to normal user tasks and active multi-instance children. Assigning snapshots a
  case-insensitive actor id in `user_tasks.Assignee`, clears any claim, and makes
  the item directly assigned; the manager does not need the task's node role.
  Unassigning clears both assignee and claimant and restores the authored node's
  `requiresClaim` behavior without re-evaluating an assignee expression, claim
  inheritance, or assignment inheritance. A required-assignment task therefore
  returns to the hidden distributor queue. Mutations require the task's expected `UpdatedAt`; stale writes
  return 409, while an exact desired-state retry is an unchanged 200 response.
  Each real change writes a `taskAssignment` instance-history row with the
  manager, previous/new ownership, and optional reason. A missing or empty
  `taskAssignmentRoles` list disables management for that workflow version.
- **External task distribution.** A workflow definition may declare top-level
  `taskDistribution: { clientId, clientSecret }`. These values may be literals
  or `${setting.*}` / `${config.*}` templates; literal secrets are retained in
  the immutable definition JSON and should be avoided in production. The
  anonymous `/api/task-distribution/workflows/{workflowKey}/tasks` API validates
  `X-Client-Id` / `X-Client-Secret` against the current published default
  definition, then lists or mutates active tasks from every version sharing that
  stable workflow key. Credential templates resolve only against trusted
  configuration/settings, never caller data, instance variables, or `sys.*`.
  The external client needs neither `taskAssignmentRoles` nor node roles.
  Assignment mutations reuse the manager transaction, stale-write protection,
  desired-state retry behavior, and multi-instance ownership checks. Real
  changes record the client id as `PerformedBy` and `authority=taskDistribution`
  in the audit payload. Missing configuration disables this machine API for the
  workflow family. Settings are freshly read during machine authentication so
  credential rotation takes effect on the next request. Its task list includes
  unassigned `requiresAssignment` work even though regular inboxes omit it.
  Instance-facing workflow details redact `taskDistribution.clientSecret`.
- Authentication: the API validates a bearer JWT (`Microsoft.AspNetCore.Authentication.JwtBearer`)
  using a shared symmetric key (`Jwt:Key`, dev only) and requires it on the
  `/api/instances` group. The Blazor UI mints its own token from the `/token`
  page (`DevTokenFactory` + `TokenState`) so a tester can switch user/roles on
  the fly; `AuthTokenHandler` attaches it as a `Bearer` header. For production,
  swap `AddJwtBearer` to a real OIDC identity provider and remove the UI minting
  page. The canonical workflow actor may be selected with the process-latched
  `flowbit.engine_settings` row `Authentication.UserIdentityClaim` (namespace
  `Authentication`, key `UserIdentityClaim`, value such as `sub` or `oid`). The
  setting is loaded at API startup; when absent, identity retains the legacy
  `Identity.Name` then `NameIdentifier` behavior. When configured, the token must
  contain one nonblank, unambiguous value no longer than 300 characters or the
  request fails with 401. That identity drives `sys.user`, inbox/assignee matching,
  claims, completions, and actor audit fields. Roles and allowlisted `sys.claim.*`
  values remain independent. `GET /api/auth/context` returns the resolved actor to
  the Blazor UI. Changing the claim requires restarting every API replica and must
  be treated as an identity migration for active work.

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
  the user JWT; returns a slim `MessageStartAckDto`; a configured idempotency or
  business-key duplicate returns `StartConflictDto` with 409 and `Location`).
- `WorkflowInstanceEndpoints` (`/api/instances`): `POST /` (start; optional
  `startEventId`; a configured idempotency value is accepted only through its
  HTTP header and a duplicate returns `StartConflictDto` with 409/`Location`),
  `GET /?status=&instanceId=&workflowId=&workflowKey=&businessKey=&nodeId=&nodeExternalId=&var=&includeVariables=&sort=&page=&pageSize=` (paged),
  `GET /inbox?instanceId=&workflowId=&workflowKey=&businessKey=&nodeId=&nodeExternalId=&var=&includeVariables=&sort=&page=&pageSize=` (paged, actor-scoped), `GET /{id}`,
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
  offset-based. Each endpoint accepts up to three repeated `sort=field:direction`
  clauses, with case-insensitive `asc` or `desc` directions. Instance sortable
  fields are `id`, `createdAt`, and `updatedAt`; inbox sortable fields are
  `userTaskId`, `instanceId`, `taskCreatedAt`, `taskUpdatedAt`,
  `instanceCreatedAt`, and `instanceUpdatedAt`. Instance ordering defaults to
  `updatedAt:desc`; inbox ordering defaults to `taskUpdatedAt:desc`. Unknown,
  malformed, blank, or duplicate clauses and more than three clauses return 400.
  If instance `id` or inbox `userTaskId` is not an explicit clause, the
  repository appends it in the final clause's direction as a deterministic
  paging tie-breaker. Composite timestamp/id indexes support the default and
  common single-field timestamp order paths; other multi-field combinations may
  still require PostgreSQL to sort the filtered result.
  `includeVariables=true` adds a compact `variables` dictionary with only the
  latest JSON value per name; otherwise that property is omitted. The instance
  list loads variables in one page-bounded query only when requested. The inbox
  always projects page-bounded latest variables to evaluate stored-state action
  visibility, so returning them adds no database query.
  `InboxItemDto` exposes `TaskCreatedAt`, `TaskUpdatedAt`, `InstanceCreatedAt`,
  and `InstanceUpdatedAt`; its legacy `CreatedAt` and `UpdatedAt` properties remain
  aliases for the task timestamps.
  Inbox role/assignment/claim filtering, counting, ordering, and paging all run
  in PostgreSQL. The service loads definitions only for the returned page to
  calculate outgoing-flow `CanAct`/`CanClaim` flags.
- `UserTaskEndpoints` (`/api/user-tasks`):
  `GET /manage?taskId=&instanceId=&workflowId=&workflowKey=&businessKey=&nodeId=&nodeExternalId=&owner=&ownership=&var=&page=&pageSize=`
  returns a role-scoped page of active work items for assignment managers;
  `POST /{taskId}/assign` accepts `actorId`, `expectedUpdatedAt`, and an optional
  `reason`; `POST /{taskId}/unassign` accepts `expectedUpdatedAt` and an optional
  `reason`. Unauthorized workflow managers receive 403 and stale mutations
  receive 409.
- `TaskDistributionEndpoints`
  (`/api/task-distribution/workflows/{workflowKey}/tasks`): `GET /` returns a
  paged family-scoped list using the same task/instance/workflow-version,
  business-key, node, owner, ownership, and repeated `var=` filters as task
  management. `includeVariables=true` adds the latest instance variables only
  for the bounded page; the property is otherwise omitted. `POST
  /{taskId}/assign` and `POST /{taskId}/unassign` reuse the manager request and
  acknowledgement DTOs. All three endpoints authenticate through
  `X-Client-Id` / `X-Client-Secret`, return 401 for invalid credentials, 404 for
  unknown or cross-family targets, and 409 for stale/inactive task conflicts.
- `MultiInstanceExecutionEndpoints` (`/api/multi-instance-executions`):
  `GET /{executionId}/flows` returns the current actor's selectable parent-level
  interrupt flows for an active execution; `POST /{executionId}/flows/{flowId}`
  validates roles/condition/variables, atomically cancels unfinished child items,
  and advances the parent token through the interrupting flow.
- **Variable search.** Both list endpoints accept repeated `var=name:value`
  query params (split on the first `:`, so values may contain `:`). Each pair is
  an exact, case-insensitive match on an instance variable's latest scalar value;
  when several are supplied they are AND-combined. Matching selects the greatest
  variable-row `Id` for the requested name before comparing its JSON scalar text,
  with all values bound as parameters. The inbox filter is additive on top of the
  actor role/claim scope.
  For the list endpoint the `var` filter combines with `status`. Malformed
  entries (missing `:` or empty name) are rejected via `WorkflowDomainException`.
  Array/object variables never match, and value ranges/operators are out of
  scope. The `(InstanceId, VariableName, Id DESC)` and
  `(VariableName, InstanceId)` indexes back the latest lookup and name search.
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
  `nodeId=` query param. The instance list matches the projected execution-token
  `NodeId`; the inbox matches the active user-task `NodeId`. A null / absent value
  applies no filter. It AND-combines with `status` / `nodeExternalId` / `var`
  (and the inbox actor scope). Because the node id is version-scoped, it is most
  meaningful combined with a specific workflow. Token/task node indexes back the
  lookup.
- **Current-node externalId search.** Both list endpoints accept an optional
  `nodeExternalId=` query param, an exact, case-insensitive match on the execution
  token (instance list) or active user task (inbox). Empty/whitespace input applies
  no filter. It AND-combines with `status` / `var` (and the inbox actor scope).
  Because externalId is a version-stable integration key it survives definition
  re-versioning (unlike the integer node id). Token/task external-id indexes back
  the lookup.
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
  params). A reusable sort toolbar applies up to three instance sort clauses and
  resets to `updatedAt:desc`.
- `/inbox` (`Inbox.razor`) - actor-scoped inbox, with the same instance id,
  workflow id, workflow key, node id, node external id, and comma-separated
  `name:value` variable filter boxes. Its sort toolbar exposes the six inbox sort
  fields and resets to `taskUpdatedAt:desc`.
- `/task-management` (`TaskManagement.razor`) - workflow-role-scoped manager
  view for filtering active tasks and assigning, reassigning, or unassigning an
  item with optimistic concurrency and an optional audit reason.
- `/instances/{id}` (`InstanceDetail.razor`) - claim/unclaim, take available
  sequence flows, take authorized parent-level multi-instance interrupt actions,
  and view variables and history, including task-assignment audit details.

The UI talks to the API through `WorkflowApiClient` (a typed `HttpClient`).

To run locally from `Flowbit/`:

```powershell
docker compose up -d
dotnet run --project .\src\Flowbit.Api\Flowbit.Api.csproj --launch-profile http
dotnet run --project .\src\Flowbit.Ui\Flowbit.Ui.csproj --launch-profile http
```

For instance throughput tests, start the API with the `LoadTest` environment so
Serilog uses Warning level and console/file I/O does not dominate the result:

```powershell
dotnet run --no-launch-profile --project .\src\Flowbit.Api\Flowbit.Api.csproj -- --environment LoadTest --urls http://localhost:5017
dotnet run --project .\tools\InstanceLoadTest\InstanceLoadTest.csproj -- --count 1000 --concurrency 32
dotnet run --project .\tools\InstanceLoadTest\InstanceLoadTest.csproj -- --count 200000 --concurrency 32
# For an already-started cardinality multi-instance task whose normal outcome is flow 201:
dotnet run --project .\tools\InstanceLoadTest\InstanceLoadTest.csproj -- --multi-instance-id 42 --flow-id 201 --concurrency 64
```

Use the first run as warm-up; short cold runs include JIT, connection-pool, and
database-cache startup costs and are not representative.

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
  "roles": [ "Requester" ],    // free-text candidate roles (userTask / user-initiated startEvent)
  "requiresClaim": false,      // if true, one user must claim before acting (userTask only)
  "claimMode": "fresh",        // userTask + requiresClaim: fresh | previous | fromNode (claim inheritance)
  "inheritClaimFromNodeId": null, // fromNode mode only: user-task node whose claimant is reused
  "requiresAssignment": false, // if true, hide until explicitly/automatically assigned (normal userTask only)
  "assignmentMode": "fresh",   // requiresAssignment: fresh | previous | fromNode
  "inheritAssignmentFromNodeId": null, // assignment fromNode mode only
  "assignee": null,            // normal userTask only: optional NCalc username expression
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
  JS value (number/string/boolean/array/object/null). A nullable process variable
  initialized to JSON `null` returns JavaScript `null`.
- `execution.setVariable(name, value)` - writes a **declared process variable**
  (`model.variables`); the JS value is marshalled back to JSON and coerced to the
  target's declared `dataType`/`isArray`, exactly like an NCalc assignment. An
  undeclared target throws (rolls back the transition). A null write is accepted
  only when the target declares `nullable: true`.
- `execution.getVariables()` - a snapshot object of all currently visible
  variables (property access, e.g. `vars.amount`).
- `execution.hasVariable(name)` - existence check. It returns `true` for a
  persisted nullable variable whose current value is JSON `null`.
- `execution.getFlowInfo(flowId)` - returns the read-only instance-lifetime flow
  summary described above; for example,
  `execution.getFlowInfo(201).actions.last.userRoles`.
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
- **`exclusiveGateway`**: routing node; diamond on canvas. It may have several
  incoming paths and requires two or more outgoing flows: every non-default flow
  has a `condition` and unique positive `conditionPriority`, plus exactly one
  `isDefault` fallback. The engine evaluates lower priorities first. This is a
  merge-then-split XOR; a pure one-outgoing merge is intentionally unsupported.
  No user interaction.
- **`endEvent`**: terminal; thick-ring circle. Requires at least one incoming
  flow and has no outgoing flows.
- **`errorEndEvent`**: terminal throwing event; thick-ring circle with a filled
  error glyph. Requires at least one incoming flow and no outgoing flows. It has
  a required stable `errorCode` and optional `errorDescription`; entering it
  ends the instance with `Faulted` (vs `Completed` for a plain `endEvent`) and
  snapshots the resolved fault metadata for API projections. Typically reached
  via an `errorBoundaryEvent`'s error path, directly or through a handler task.
- **`errorBoundaryEvent`**: an error catch attached to a `serviceTask` or
  `scriptTask` (`attachedToRef`); a small double-ring circle with an outlined
  catching error glyph drawn on the host's border. When the host fails at runtime (HTTP non-2xx/timeout/network,
  or a script/assignment/validation error) the token routes out the boundary's
  single outgoing **error** flow instead of failing the transition, with an
  optional `errorVariable` capturing the failure reason. With no boundary
  attached, a failure fails the transition (rollback + 400). A boundary is
  pass-through (auto-advances its single unconditional error flow) and may have
  no incoming flows; at most one boundary per host.
- **`intermediateMessageCatchEvent`**: a resting node (like a `userTask`) that
  waits for an external system to deliver a message via
  `POST /api/instances/{id}/message`; a thin double-ring circle with an envelope
  glyph on canvas. Carries a `message` config (clientId, clientSecret, headerName,
  headerValue, headerValidation, outputMappings) and exactly one unconditional
  outgoing flow. The instance rests on it until a matching message is delivered;
  the caller authenticates against the templated client credentials + required
  header, the payload is mapped into instance variables through atomic typed
  `outputMappings`, and the engine advances down the flow. No user
  action. Correlation is by instance id only (no cross-instance signal/message
  matching). No timeout escape hatch yet.
- **`messageStartEvent`**: an entry point (like a `startEvent`) started by an
  external system via `POST /api/workflows/{workflowKey}/message-start`; a thin
  single-ring circle with an envelope glyph on canvas. Its `message.outputMappings`
  are typed start-variable declarations; there is no separate node `variables`
  section. It has exactly one unconditional outgoing flow. System-only: it is not a `startEvent` (`IsStart` is
  false), so it cannot be started via `POST /api/instances` and does not appear in
  the Blazor start picker. The engine creates the instance and auto-advances off
  it (pass-through, history note `messageStart`). The caller authenticates against
  the templated client credentials + required header; mappings strictly type payload
  values, apply ordered defaults, enforce required values, and run NCalc validation.
  Like a normal start event it may independently enable node-level transport
  `idempotency` and/or a domain `businessKey`.

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
  "roles": [ "Manager" ],      // userTask flow: enforced at runtime (empty = anyone)
  "variables": [ /* Variable[] */ ], // userTask flow: data captured when taken
  "condition": "amount > 1000",// userTask / exclusiveGateway flow only (nullable)
  "conditionPriority": 1,      // exclusiveGateway non-default only; lower runs first
  "isDefault": false,          // userTask / exclusiveGateway default flow
  "isSelectable": true         // multi-instance user action; false = engine-only
}
```

### Variable
Typed data attached to a start event (node) or a user-task sequence flow.
These input variables cannot declare `nullable: true`; required inputs reject
null, while optional JSON inputs retain their existing JSON-value behavior.

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
instance-scoped: never supplied by a user, initialized from a concrete
`defaultValue` or a declared nullable null at instance start, and mutated by
script tasks and typed service/message outputs during pass-through routing.
They are visible to gateways, service-task templates, and validation rules
through the same `WithContext` overlay as any other stored variable.

```jsonc
{
  "id": 1,
  "name": "creditScore",
  "dataType": "number",
  "isArray": false,
  "nullable": true,
  "defaultValue": null,
  "validation": "creditScore > 0"
}
```

Process variables differ from start/flow variables in a few respects:

- `required` is meaningless (the value is computed, not collected) and is hidden
  in the editor. `ValidateDefinition` does not reject it but the engine ignores
  it; the editor forces `required: false`.
- `nullable` is optional and defaults to `false`. Non-nullable process variables
  require a concrete `defaultValue`. For `nullable: true`, an omitted or null
  default initializes and persists JSON `null`; a concrete default is still
  allowed and type-checked. Nullability applies to the whole value, so an array
  may itself be null while a non-null array still enforces its element type.
- Concrete defaults are templated + coerced like start-variable defaults, so
  `${sys.user}` / `${amount}` placeholders work. Every process declaration,
  including a null one, is persisted and readable from hop 0.
- NCalc and JavaScript script writes, plus service-task and intermediate-message
  mappings that target a process variable, enforce its `nullable` and type/array
  contract. An optional mapping may write an explicitly extracted null to a
  nullable target; a missing path still performs no write, and `defaultValue:
  null` remains no operation-specific fallback. `required: true` mappings reject
  null even for nullable targets.
- `validation` runs after start initialization and each mapping/script write when
  the value is concrete. Accepted null values skip both the operation mapping's
  rule and the process-variable rule until a concrete value is written.
- The editor surfaces them in the "Workflow" inspector panel (shown when nothing
  is selected), separate from the per-node / per-flow variable editors.

### ServiceTaskConfig
The connector configuration on a `serviceTask` flow node (`flowNode.service`).
The editor currently offers only REST, but persists the connector discriminator
so additional protocols can be added without changing the node shape.

```jsonc
{
  "type": "rest",              // connector type; REST is the only supported value today
  "method": "POST",            // GET | POST | PUT | PATCH | DELETE
  "url": "https://api.example.com/credit/${customerId}", // ${var} templated
  "headers": [                 // ${var} templated values
    { "name": "Authorization", "value": "Bearer ${apiToken}" }
  ],
  "body": "{ \"amount\": ${amount} }", // JSON template; ${var} -> variable's JSON value
  "timeoutSeconds": 30,        // per-call timeout (no retries; deployment-capped)
  "statusVariable": "creditStatus", // optional; receives the HTTP status (0 before a response exists)
  "outputMappings": [          // typed response field -> instance variable (applied on 2xx)
    { "variable": "creditScore", "path": "score", "dataType": "number", "isArray": false, "required": true, "defaultValue": null, "validation": "creditScore >= 0" },
    { "variable": "approved", "path": "decision.approved", "dataType": "boolean", "isArray": false, "required": false, "defaultValue": false, "validation": null }
  ]
}
```

Each mapping declares `variable`, `path`, `dataType`, `isArray`, `required`, an
optional operation-specific `defaultValue`, and optional NCalc `validation`.
External values must strictly match the declared JSON type (`date` is
`YYYY-MM-DD`; `datetime` is ISO-8601). A missing path uses its default; a blank
path is allowed only with a default. Required applies to the current response or
that default, never an older stored value. Defaults resolve in mapping order and
all validations see the final overlay. The complete batch is validated before
any write. A failure routes through an attached error boundary or otherwise
rolls back with 400; `statusVariable` remains available to the error path.

A `serviceTask` may also have an attached `errorBoundaryEvent` (see Error
events); on a non-2xx/timeout/network failure the token routes out the
boundary's error flow instead of failing the transition. The legacy `onError`
field has been removed (old values load tolerantly and are ignored).

In `url` and header values a `${var}` placeholder becomes the variable's scalar
text and a missing variable fails the task before an outbound request is sent.
In `body` substitution is quote-aware: a placeholder inside a JSON string
literal becomes the variable's escaped scalar text with no added quotes (so
`"message": "Hi ${sys.user}"` -> `"message": "Hi alice"`), while a placeholder in
a bare value position becomes the variable's JSON representation (so
`"amount": ${amount}` stays an unquoted number). A missing variable is an empty
string inside a string and `null` in a bare position. Output `path` is dotted
(`a.b.c`), with numeric segments indexing into arrays (`items.0.id`). Response
bodies are read only up to `WorkflowServiceTasks.MaxResponseBodyBytes`.

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
  "outputMappings": [
    { "variable": "approved", "path": "decision.approved", "dataType": "boolean", "isArray": false, "required": true, "defaultValue": null, "validation": null },
    { "variable": "reference", "path": "ref", "dataType": "string", "isArray": false, "required": false, "defaultValue": "unknown", "validation": "Len(reference) > 0" }
  ]
}
```

Canonical typed mapping example:

```json
{
  "variable": "violationId",
  "path": "violation.id",
  "dataType": "string",
  "isArray": false,
  "required": true,
  "validation": "StartsWith(violationId, 'V-')"
}
```

Message starts and intermediate catches both declare `dataType`, `isArray`,
optional `defaultValue`, and optional NCalc `validation`. Payload values must
strictly match the declared JSON type (`date` is `YYYY-MM-DD`; `datetime` is
ISO-8601). A missing path uses `defaultValue`, then fails with 400 when the final
current-delivery value is required; an optional unresolved mapping leaves any
older value unchanged. A blank path is valid only for a default-only mapping.
Catch mapping resolution, validation, and persistence are atomic; message-start
mapping resolution remains atomic with instance creation.

Transport idempotency is configured on either entry node, outside `message`:

```json
"idempotency": {
  "headerName": "Idempotency-Key",
  "variable": "requestId"
}
```

The legacy message-only `idempotencyVariable` property is accepted only for
in-memory normalization of historical definitions and is never emitted by new
saves.

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
caller is by then authenticated); then resolves the typed `outputMappings`
against the JSON message body and final stored/context overlay. Strict type,
required/default, or mapping/process NCalc failures reject the delivery with a
`WorkflowDomainException` (400) before any variables are written. A client
id/secret mismatch throws `WorkflowUnauthorizedException` (401); a header problem
(missing/mismatch/validation failure), a required-mapping failure, or a
not-running / not-waiting instance throws `WorkflowDomainException` (400). The
resolved client id is recorded as `performedBy` / `sys.user` for attribution.
The endpoint returns a slim `MessageDeliveryAckDto` (`Id`, `CurrentNodeId`,
`CurrentNodeName`, `CurrentNodeExternalId`, `Status`, `UpdatedAt`) rather than
the full `InstanceDetailDto`, so a node-credentialed webhook caller cannot read
the workflow definition (which may contain other nodes' literal secrets) or the
instance's stored variables/history. Authenticated instance-detail responses
deep-clone the definition and redact message `clientSecret`/`headerValue`; admin
definition endpoints remain unchanged. A catch may opt in to instance-scoped,
permanent receipts with `message.deliveryIdempotency=true` and a configurable
`message.deliveryIdempotencyHeaderName`; the key remains internal transport
metadata and does not create an implicit workflow variable. Committed key reuse
returns 409. Request bodies are strict JSON and bounded by
`WorkflowMessageDelivery.MaxPayloadBytes` (default 1 MiB).

**Message-start delivery** (`POST /api/workflows/{workflowKey}/message-start`,
`AllowAnonymous`): same auth mechanics, but the credential/header templates are
resolved against an instance-less context (`config.*`/`setting.*` +
non-caller-influenced `sys.*`; no `sys.instanceId`/`sys.user`/`sys.roles` since
there is no caller/instance yet). Typed `outputMappings` declare, resolve, validate,
and persist the message-start variables directly.
When the selected entry has node-level `idempotency`, the engine reserves the
trimmed transport key in `workflow_idempotency_claims` before its business key.
A permanent duplicate returns 409 with `code: "idempotency_conflict"`, its
`instanceId`, and a `Location` header instead of creating another instance. A
duplicate domain business key similarly returns 409 with
`code: "business_key_conflict"` and the owning `instanceId`.
A slim `MessageStartAckDto` (`InstanceId`, `CurrentNodeId`, `CurrentNodeName`,
`CurrentNodeExternalId`, `Status`, `CreatedAt`, and an optional `Fault`) is
returned (never the full
definition/variables/history, since the endpoint is `AllowAnonymous`).
The published default definition is selected. Non-empty bodies require a JSON
media type and are bounded by `WorkflowMessageDelivery.MaxPayloadBytes`.

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
| `type: "startEvent"` | None Start Event | Entry marker; thin-ring circle. Carries start `variables` (BPMN would model these as data inputs / form fields). `roles` are enforced at runtime against JWT role claims (empty = open to anyone). May enable the engine's node-level transport-idempotency and domain-business-key extensions. |
| `type: "userTask"` | User Task | Human-performed activity; rounded rectangle with a user marker. |
| `type: "task"` | Abstract/automatic Task | Pass-through activity completed with no user action; closest to a BPMN Task without an implementation. |
| `type: "serviceTask"` | Service Task | Automatic REST call (SVC marker); templated request from variables, response mapped back into variables. Simplified: REST only, synchronous, no retries. |
| `type: "scriptTask"` | Script Task | Automatic variable mutation (SCRIPT marker); either NCalc assignments or a Jint-run JavaScript body (`scriptFormat`) writes process variables during the pass-through hop. Simplified: both run in-process (Jint, sandboxed, no CLR) rather than spawning an external script engine/process. |
| `type: "exclusiveGateway"` | Exclusive Gateway (XOR) | Diamond; permits multiple incoming paths and routes by ascending condition priority, else the required default flow. Requires at least two outgoing flows, so a pure merge is not modeled. |
| `type: "endEvent"` | None End Event | Terminal marker; thick-ring circle. Requires an incoming flow and has no outgoing flow. |
| `type: "errorEndEvent"` | Error End Event | Terminal throwing marker; thick-ring circle with a filled error glyph. Requires an incoming flow, has no outgoing flow, and ends the instance with `Faulted`. Its required static `errorCode` and optional description are operational fault metadata; there is no subprocess propagation, so it is normally reached through an explicitly modeled error path. |
| `type: "errorBoundaryEvent"` | Error Boundary Event (interrupting) | Attached to a `serviceTask`/`scriptTask`; catches the host's runtime failures and routes out the boundary's single error flow. Simplified: interrupting only; catch-all (no error code match); at most one per host; no other boundary trigger types (timer/message/signal) yet. |
| `type: "intermediateMessageCatchEvent"` | Intermediate Message Catch Event | A resting node that waits for a message delivered via `POST /api/instances/{id}/message`; thin double-ring circle with an envelope glyph. Auth is the node-config client id/secret + a required custom header (with optional NCalc validation), not the user JWT. Simplified: correlation by instance id only (no cross-instance message-name/signal matching); no timeout escape hatch (a future timer boundary could address). |
| `type: "messageStartEvent"` | Message Start Event | An entry point started by an external system via `POST /api/workflows/{workflowKey}/message-start`; thin single-ring circle with an envelope glyph. Typed `message.outputMappings` declare its start variables. System-only (`IsStart` is false). The engine creates the instance and auto-advances off it (pass-through, history note `messageStart`). Simplified: instance-less credential resolution (no `sys.user`/`sys.roles`/`sys.instanceId` for credentials since there is no caller/instance yet). It shares the same optional node-level, database-claimed transport idempotency as `startEvent`. |
| `sequenceFlow` | Sequence Flow | First-class directed edge with its own id, `sourceRef`, `targetRef`. |
| `sequenceFlow.condition` | Condition Expression | NCalc expression on user-task and gateway flows (comparisons, boolean/arithmetic operators, functions, bare-variable truthiness). |
| `sequenceFlow.conditionPriority` | Engine extension | Unique positive evaluation order for non-default exclusive-gateway flows; lower values run first. Legacy all-missing values derive from JSON array order. |
| `sequenceFlow.isDefault` | Default Flow | The gateway's fallback path; on a user-task flow it means the action is always visible regardless of condition. |
| `sequenceFlow.isSelectable` | Engine extension | On a multi-instance user-task flow, `false` makes the route engine-only: not a user action, but still eligible for aggregate completion/default routing. Defaults to `true`. |
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
  and `messageStartEvent` (instance-less system-only entry with optional
  database-claimed workflow-family idempotency); no timer/signal events yet.
- **No pools / collaboration.** Lanes exist without a multi-party pool or message
  flow.
- **NCalc condition language.** Gateway conditions are evaluated with NCalc, so
  comparisons (`== != < <= > >=`), boolean operators (`and`/`or`), arithmetic,
  parentheses, functions, and quoted string literals are all supported (a bare
  variable name is still a truthiness check). DMN-style decision tables remain
  out of scope.
- **Integer ids, JSON (not BPMN XML).** Flow nodes and sequence flows use integer
  ids so runtime tables stay integer-keyed; the definition is JSON, not BPMN XML.
- **Node roles are enforced** at runtime (against JWT role claims) for both
  `userTask` nodes and user-initiated `startEvent` nodes; an empty `roles` list
  means open to anyone. Sequence-flow `roles` are enforced at runtime for
  `userTask` flows (same semantics), stacking on top of the task's own node
  roles and claim ownership; `userTask` flow conditions are stored-state
  visibility rules rechecked at execution time before submitted action values are
  applied. `canActWithoutClaimRoles` optionally restricts which otherwise-authorized
  actors can use a claim-bypass flow. `requiresClaim` ownership is enforced on top
  of the role check unless that explicit bypass succeeds.

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
  (enforced at runtime in `Flowbit/`). Hidden for other node types.
- **Automatic task / start event flow**: `task` and `startEvent` each own one
  unconditional outgoing flow, drawn as a dashed edge. The engine follows it in
  the pass-through loop with `automatic` / `start` history notes. `serviceTask`
  and `scriptTask` are also pass-through (single unconditional outgoing flow),
  logged with `service` / `script` notes.
- **Message catch flow**: an `intermediateMessageCatchEvent` owns one
  unconditional outgoing flow, drawn as a solid (non-dashed) edge since the node
  rests (it is not pass-through). The engine advances down it on message
  delivery, logged with a `message` history note.
- **Gateway flows**: `exclusiveGateway` outgoing flows carry a `condition` plus
  unique positive `conditionPriority`, or the required `isDefault` marker. The
  editor shows priority/default metadata beneath the edge and enforces exactly
  one default per gateway. Several paths may enter the same gateway; it then
  evaluates the same outgoing decision for whichever path arrived.
- **User-task flows**: `userTask` outgoing flows may carry a stored-state `condition`
  (NCalc), action roles, and optional claim-bypass roles; the editor shows these
  controls beneath the edge. Multi-instance flows may set
  `isSelectable=false`; the editor marks these edges `engine-only`, the API never
  exposes or accepts them as user actions, and only completion/default routing may
  choose them.
- **User-task assignee**: a normal `userTask` may carry an `assignee` NCalc
  expression such as `approverUser`, `'alice'`, or
  `if(amount > 1000, managerUser, supervisorUser)`. It is evaluated once on task
  entry and the result is snapshotted on the work item. The editor shows an
  `ASGN` marker and an `Assignee expression` field. Legacy user-task node
  `condition` properties are ignored; sequence-flow conditions remain supported.

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
changes belong under `Flowbit/`.
