# Flowbit workflow example catalog

This directory is the canonical catalog of editor-loadable, runtime-valid
Flowbit workflow definitions. Each JSON file uses the current editor save
format, an `example-*` workflow id, integer node and sequence-flow ids, and no
legacy properties. The examples are intentionally focused: combine their
patterns when building a production workflow.

The catalog contains 31 workflows. Automated tests recursively discover every
JSON file, deserialize it, validate it with the runtime's strict JavaScript
parser, load and render it in the editor, and verify that this page links it.
Tests never call the REST or message endpoints described below.

## Load and run an example

1. Open `flowbit-editor.html` and choose **Load JSON**.
2. Select a definition below, inspect it, and save it without validation errors.
3. Publish the definition through the API or Blazor UI and start an instance.
4. Use an authenticated actor with the documented roles to complete its work.
5. Run `Flowbit.Worker` for timers and durable jobs. Other examples need only
   the API and PostgreSQL unless their row says otherwise.

Dates, timer intervals, and retry delays are short or illustrative local-test
values. Adjust them before production use.

## Basics

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Lanes, external IDs, and variable scopes](basics/01-lanes-external-ids-and-variable-scopes.json) | As `Requester`, supply required `requestId`; optional typed values have defaults. | A `FinanceReviewer` approves an eligible stored request with `approvalNote`; a `FinanceManager` may approve or escalate with `escalationReason`. | Demonstrates lanes; node/flow `externalId`; process, start, and action scopes; every data type; arrays; a nullable process value; defaults; validation; and stored-state action conditions. | API and database; no Worker or configuration. |

## User tasks

See the [user-task guide](user-tasks/README.md) for endpoint examples and actor
walkthroughs.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Roles, claim, and bypass](user-tasks/01-roles-claim-and-bypass.json) | `Requester` supplies required `requestReference`. | A `Reviewer`/`Supervisor` claims and reviews; `Supervisor` can use the protected claim-bypass flow and unclaim; `WorkflowAdministrator` can cancel. | Normal and bypass routes complete at distinct end events while start, node, flow, claim-bypass, cancellation, and unclaim roles are enforced. | Authenticated API and database; no Worker or configuration. |
| [Claim inheritance](user-tasks/02-claim-inheritance.json) | `Coordinator` starts with no values. | Actor A claims the fresh task and inherits `previous`; actor B claims the specialist task; the final `fromNode` task returns to actor A. | Demonstrates distinct actors and `fresh`, `previous`, and `fromNode` claim modes. | Authenticated user-task API and database; no Worker or configuration. |
| [Direct assignee expression](user-tasks/03-direct-assignee-expression.json) | `Requester` supplies `requestSummary`; `requestedAssignee` has a padded default and may be blank. | The resolved `Reviewer` acts. `Lower`, `Trim`, `IsNullOrWhiteSpace`, variables, and `[sys.user]` normalize the direct assignee. | Only the snapshotted assignee can complete the review. | Authenticated API and database; no Worker or configuration. |
| [Required assignment and distribution](user-tasks/04-required-assignment-and-distribution.json) | `Requester` supplies required `workReference`. | A distributor assigns the fresh task; `Worker` completes inherited work; `OperationsManager` handles the independent task; manager roles can assign/release. | Shows required assignment plus `fresh`, `previous`, and `fromNode` assignment ownership. | API and database; configure `${config.exampleDistributorClientId}` and `${config.exampleDistributorSecret}` for the external distribution endpoints; no Worker. |

## Script tasks

See the [script-task guide](scripts/README.md) for expected values, the complete
JavaScript execution API, FlowInfo shapes, and atomic error behavior.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [NCalc helper functions](scripts/01-ncalc-helper-functions.json) | No values; typed process defaults provide input. | `ExampleReviewer` inspects results and finishes. | Ordered assignments exercise `Length`/`Len`, null/whitespace checks, contains/prefix/suffix, case conversion, trim, regex, and representative built-ins. | API and database; no Worker or configuration. |
| [JavaScript execution API](scripts/02-javascript-execution-api.json) | No values; defaults provide typed bridge data. | `ExampleReviewer` inspects and finishes. | Uses `getVariable`, `setVariable`, `getVariables`, and `hasVariable`, writing validated string, number, boolean, date, datetime, array, and JSON values. | API and database with the configured Jint sandbox; no Worker or external configuration. |
| [FlowInfo in NCalc and JavaScript](scripts/03-flowinfo-ncalc-and-javascript.json) | No values. | `ExampleReviewer` selects **Accept** or **Return**. | NCalc and JavaScript inspect action/traversal evidence; an Exclusive gateway routes according to the selected flow. | API and database; no Worker or configuration. |
| [JavaScript error boundary](scripts/04-javascript-error-boundary.json) | No values. | `ExampleOperator` acknowledges the handled failure. | A deliberate script exception rolls back staged writes, populates the boundary error variable, and follows the error path. | API and database; no Worker or configuration. |

## Service tasks

See the [service-task guide](service-tasks/README.md) for safe local mock request
and response contracts. The definitions contain no public service URL or secret.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [REST templating and typed output](service-tasks/01-rest-templating-and-typed-output.json) | `ExampleRequester` supplies required `requestId` and `customerId`; other request values have defaults. | `ExampleReviewer` completes review after a successful call. | URL/header/body interpolation feeds a REST call; status plus declared/dynamic typed mappings, arrays, defaults, and validation commit atomically. | API, database, controlled mock API, `${config.exampleApiBaseUrl}`, and `${config.exampleApiToken}`; no Worker. |
| [REST error boundary and error end](service-tasks/02-rest-error-boundary-and-error-end.json) | `ExampleRequester` supplies `requestId` and `resourceId`. | `ExampleOperator` retries after fixing the mock or ends with a domain fault. | HTTP status and error variables are captured; retry can succeed, while the terminal route produces `EXAMPLE.REST_FAILURE`. | API, database, controlled failing mock, and the same two trusted REST settings; no Worker. |

## Messages

See the [message guide](messages/README.md) for headers, payloads, duplicate
delivery behavior, and local test calls.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Typed message start](messages/01-message-start-typed-mapping.json) | Deliver the documented order payload to the system-only message start with its correlation and `Idempotency-Key` headers. | No actor starts it; `OrderReviewer` accepts the imported order. | Typed payload fields map atomically, `orderId` becomes an active-scope business key, and duplicate start delivery is rejected by its independent idempotency key. | API and database; the two `setting.examples.*` values have seeded defaults, while `${config.exampleMessageClientSecret}` must be configured; no Worker. |
| [Message catch delivery idempotency](messages/02-message-catch-delivery-idempotency.json) | `Purchasing` starts with a `PO-*` `purchaseOrderId`, then sends the order. | The authenticated external client delivers a typed receipt; `Purchasing` verifies it. | The catch validates credentials/header, maps values, and accepts a delivery key once; a duplicate `X-Flowbit-Delivery-Key` delivery is rejected. | API and database; the message client id has a seeded default, while `${config.exampleMessageClientSecret}` must be configured; no Worker. |

## Start events

See the [start-event guide](start-events/README.md) for request shapes and
conflict semantics.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Business-key scopes and idempotency](start-events/01-business-key-scopes-and-idempotency.json) | Choose the `active` start as `ActiveCaseStarter` with `ACTIVE-*`, or the `all` start as `PermanentCaseStarter` with `PERM-*`; send that start's transport-idempotency header. | `CaseWorker` completes the shared task; `CaseManager` may cancel. | Active-scope business keys can be reused after termination; all-scope keys and both independent transport-idempotency keys stay reserved across versions. | Authenticated API and database; no Worker or configuration. |

## Gateways and scoped interruption

The [gateway guide](gateways/README.md) explains topology and suggested action
sequences for all six definitions.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Exclusive priority and default](gateways/01-exclusive-priority-and-default.json) | Set `urgent` and `amount`. | Complete the task selected by the Exclusive split. | Priority 1 wins before the amount condition; otherwise the required default is used, and a separate merge rejoins the routes. | API and database; no roles, Worker, or configuration. |
| [Parallel fork and join](gateways/02-parallel-fork-and-join.json) | No values. | Complete Finance, Legal, and Security work in any order. | Three tokens run concurrently and the Parallel join waits for all incoming branches. | API and database; no roles, Worker, or configuration. |
| [Inclusive split and merge](gateways/03-inclusive-conditional-split-and-merge.json) | Set the Legal and Security flags. | Complete every selected review. | Every true route is created; the default is used when none match, and the merge waits only for selected reachable branches. | API and database; no roles, Worker, or configuration. |
| [Complex two-of-three merge](gateways/04-complex-two-of-three-merge.json) | No values. | Complete any two reviewer tasks, then finalize. | `TotalIncomingCount() >= 2` activates the coordinator task; its terminate end cancels the remaining review. | API and database; no roles, Worker, or configuration. |
| [Complex start/reset cycle](gateways/05-complex-start-reset-cycle.json) | No values. | Choose reset/next-cycle or finish actions on the phase tasks. | `IncomingCount` and `[gateway.waitingForStart]` demonstrate persisted start/reset phases and repeatable cycles. | API and database; no roles, Worker, or configuration. |
| [Scoped interrupt of a parallel branch](gateways/06-scoped-interrupt-parallel-branch.json) | No values. | Leave normal investigation active and trigger the emergency branch. | The scoped interrupt resolves its nearest Parallel activation, cancels sibling work, and promotes the trigger to emergency response. | API and database; no roles, Worker, or configuration. |

## Multi-instance user tasks

See the [multi-instance guide](multi-instance/README.md) for child-task actors,
result shapes, aggregate evidence, and completion paths.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Parallel collection assignees](multi-instance/01-parallel-collection-assignees.json) | `ReviewCoordinator` supplies a string-array reviewer collection or uses its defaults. | Every directly assigned `Reviewer` completes one parallel child. | All items are available together; after all complete, outcome `201` wins and ordered result evidence is written. The engine-only default remains the required no-match safety route. | API and database; no Worker or configuration. |
| [Sequential collection assignees](multi-instance/02-sequential-collection-assignees.json) | `ReviewCoordinator` supplies the ordered reviewer collection or uses defaults. | Assigned `Reviewer` actors complete one child at a time in collection order. | Only the current child is active; after all items, outcome `201` wins and the result array preserves collection order. The engine-only default remains available only if no condition matches. | API and database; no Worker or configuration. |
| [Cardinality one per actor](multi-instance/03-cardinality-one-per-actor.json) | `ReviewCoordinator` supplies `panelSize` or uses its default. | Distinct `Panelist` actors each complete at most one representative pooled item. | Cardinality fan-out, one-per-actor inbox selection, stale-action conflict behavior, and aggregate results are visible. | API and database; no Worker or configuration. |
| [Early quorum and priorities](multi-instance/04-early-quorum-and-priorities.json) | `ApprovalCoordinator` supplies `quorumSize` or uses its default. | Distinct `Approver` actors vote; `ApprovalManager` resolves a mixed quorum. | `afterEach` evaluates `CountFlow`, `PercentFlow`, `mi.total/completed/remaining`, completion priorities, and `FlowInfo`; a winning quorum cancels unfinished items. | API and database; no Worker or configuration. |
| [Parent interrupt](multi-instance/05-parent-interrupt.json) | `ReviewCoordinator` starts the collection or uses defaults. | Assigned reviewers may work; a role-authorized `ReviewManager` takes the parent-level interrupt independently of a child item. | The interrupt advances the parent exactly once, cancels unfinished items, appends parent-interrupt evidence after child results, and retains the required engine-only default. | Authenticated API and database, including the parent multi-instance flow endpoint; no Worker or configuration. |
| [Multi-instance reminder](multi-instance/06-multi-instance-reminder.json) | No values; cardinality is three. | Complete three approvals while the non-interrupting reminder remains observable. | `afterAll` aggregate completion, a parent boundary timer, and durable execution record reminders without cancelling the parent. | API, database, and `Flowbit.Worker`; no external configuration. |

## Durable jobs and timers

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Async before and after](durable-jobs/01-async-before-after.json) | No values; `executionCount` defaults to zero. | After worker processing, complete **Inspect Result**. | `asyncBefore` queues the script; `asyncAfter` commits the automatic task before routing, with explicit retry delays and an empty retry list. | API, database, and `Flowbit.Worker`; no configuration. |
| [Intermediate timer delay](timers/02-intermediate-timer-delay.json) | No values. | Wait 30 seconds, then finish **Timer Fired**. | The token rests at an ISO-8601 duration timer and resumes through durable work. | API, database, and Worker; no configuration. |
| [Recurring timer start](timers/03-recurring-timer-start.json) | No values; use the manual start only for comparison. | Acknowledge each scheduled instance. | `R5/PT30S` creates five instances, 30 seconds apart. | API, database, and Worker with timer schedules enabled; no configuration. |
| [Absolute timer start](timers/04-absolute-timer-start.json) | No values; change the authored UTC time to a near-future instant before testing. | Acknowledge the scheduled instance. | A one-time absolute schedule creates an instance at its UTC timestamp. | API, database, and Worker with timer schedules enabled; no configuration. |
| [User-task reminder and deadline](timers/05-user-task-reminder-and-deadline.json) | No values; `remindersSent` defaults to zero. | Approve normally before one minute, or complete escalated approval after the deadline. | A recurring non-interrupting boundary records reminders while an interrupting deadline cancels the original task and escalates. | API, database, and Worker; no configuration. |

## Trusted configuration placeholders

Definitions use deployment-owned values, never embedded secrets or public
services:

| Reference | Purpose |
| --- | --- |
| `${config.exampleApiBaseUrl}` | Base URL of a local/private REST mock. |
| `${config.exampleApiToken}` | Token supplied to that mock. |
| `${setting.examples.messageClientId}` | Client id accepted by message start/catch nodes; seeded as `example-message-client` when missing. |
| `${config.exampleMessageClientSecret}` | Message client secret kept outside workflow JSON. |
| `${setting.examples.messageCorrelation}` | Expected message-start correlation header value; seeded as `orders:inbound` when missing. |
| `${config.exampleDistributorClientId}` | Client id for external task distribution. |
| `${config.exampleDistributorSecret}` | Secret for external task distribution. |

## Runtime-only companion features

Some operational capabilities are not authored as workflow JSON fields. Use the
runtime documentation alongside these definitions:

- [Standing delegation policies and `/api/user-delegations`](../AGENTS.md#runtime-engine-flowbit)
- [Assignment, reassignment, release, claim, unclaim, and task actions](../Flowbit/README.md#user-tasks)
- [Instance, inbox, and advanced variable search](../Flowbit/README.md#advanced-variable-search)
- [Cross-workflow node-execution reporting](../Flowbit/README.md#node-execution-activity)
- [Immutable definition versioning and workflow-key storage](../Flowbit/README.md#storage)
- [Runtime administration and API endpoints](../Flowbit/README.md#main-api)
- [Durable jobs, schedules, retries, and worker operation](../Flowbit/README.md#durable-async-work-and-timers)

## Manual verification checklist

After automated validation, exercise integrations with local test identities and
controlled mocks:

- claim, bypass, unclaim, inherited claim, direct assignee, assignment manager,
  and distributor actors;
- REST success, typed mapping failure, caught HTTP failure, retry, and error-end
  paths;
- typed message start, catch delivery, duplicate delivery key, and duplicate
  start idempotency key;
- active/all business-key conflicts and independent transport-idempotency
  conflicts;
- both FlowInfo action routes and each multi-instance aggregate, interrupt, and
  after-all completion path;
- timers and durable retry boundaries with `Flowbit.Worker` running.
