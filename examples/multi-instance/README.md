# Multi-instance user-task examples

The numbered sequence isolates each multi-instance behavior, then combines it
with aggregate routing, parent interruption, timers, and durable jobs. All
examples keep the required engine-only default outcome visible in the diagram.

| Example | Start values | Actors and actions | Expected result | API, Worker, and configuration |
| --- | --- | --- | --- | --- |
| [`01-parallel-collection-assignees.json`](01-parallel-collection-assignees.json) | No input. Process defaults assign `alice`, `bob`, and `carol`. | Start as `ReviewCoordinator`. Each named user also needs `Reviewer` and takes flow `201` with `reviewComment`. | Three active, directly assigned items are created together. After all three complete, `parallelReviewResults` contains three item rows and the instance ends. | Published definition and API; no Worker or custom setting. |
| [`02-sequential-collection-assignees.json`](02-sequential-collection-assignees.json) | No input. Process defaults order `analyst`, `manager`, then `director`. | Start as `ReviewCoordinator`. Each named user also needs `Reviewer` and takes flow `201` with `stageComment` when their item becomes active. | Only one assigned item is active at a time, in collection order. `sequentialReviewResults` preserves item order and the instance ends after the director. | Published definition and API; no Worker or custom setting. |
| [`03-cardinality-one-per-actor.json`](03-cardinality-one-per-actor.json) | No input. `panelSize` defaults to `3`. | Start as `ReviewCoordinator`. Three distinct `Panelist` users take flow `201` with `score` and optional `comment`. | Three pooled items are completed by three distinct usernames. `panelReviewResults` records action values and action-time roles. | Published definition and API; no Worker. `Workflow.MultiInstance.MaxInstances` must be at least `3` (the default is sufficient). |
| [`04-early-quorum-and-priorities.json`](04-early-quorum-and-priorities.json) | No input. `quorumSize` defaults to `5`. | Start as `ApprovalCoordinator`. Distinct `Approver` users take `Approve` (`201`) or `Object` (`202`). `ApprovalManager` handles flow `401` when required. | Conditions run `afterEach`. Three approvals end early unless an objection already exists; then priority `1` routes to manual resolution ahead of priority `2`. Unfinished items are cancelled. If no condition wins after all items, fallback `203` is used. | Published definition and API; no Worker. Max instances must be at least `5`. |
| [`05-parent-interrupt.json`](05-parent-interrupt.json) | No input. Process defaults assign four reviewers. | Start as `ReviewCoordinator`. Named `Reviewer` users may take flow `201`. A `ReviewManager` may take parent flow `202` with `interruptReason`, without owning a child item. | Normal completion writes four item rows. A parent interrupt cancels unfinished items, appends one `parentInterrupt` result row, and rests at **Inspect Interrupt Results**. | Published definition and API; no Worker. Use the multi-instance execution endpoints for the parent action. |
| [`06-multi-instance-reminder.json`](06-multi-instance-reminder.json) | No input. Cardinality is fixed at `3`. | Complete each visible `Approve` action (`201`). The non-interrupting boundary timer requires no user action. | Aggregate conditions run `afterAll`; recurring timer branches increment `remindersSent` without completing the parent. All approvals route to the normal end. | API plus `Flowbit.Worker`. Durable job processing and timers must be enabled; the authored cycle is `R3/PT15S`. |

## Shared contract

A multi-instance user task has one parent execution token and many child work
items. Every example declares a scalar `json` process variable initialized to
`[]` for the ordered result collection. Each child result has `kind: "item"`,
its selected flow and submitted values, the completing user, and that user's
normalized action-time roles. Cancelled or otherwise non-action rows have
`userRoles: null`; an authenticated actor with no roles has `userRoles: []`.

Every definition also has exactly one pure fallback with:

```json
{
  "isDefault": true,
  "isSelectable": false,
  "completionCondition": null,
  "completionPriority": null,
  "cancelRemainingInstances": false
}
```

That route is engine-only. It does not appear in available actions and cannot be
taken through a user action endpoint. It wins only after the configured
evaluation timing when no non-default condition matches.

Collection mode snapshots a declared `string[]` and assigns one child directly
to each username. It must use `requiresClaim=false` and `claimMode="fresh"`.
Parallel mode activates every item together; sequential mode activates only the
next index. Cardinality mode evaluates an NCalc count and creates pooled role-
eligible work. With `onePerActor=true`, one authenticated username may complete
at most one item; concurrent users can temporarily see the same representative,
so a stale loser receives `409` and should refresh.

## Completing child items

Use task-specific endpoints for multi-instance work:

1. Read the actor's inbox and select a child `taskId`.
2. Read `GET /api/user-tasks/{taskId}/flows`.
3. Take an authored selectable flow with
   `POST /api/user-tasks/{taskId}/flows/{flowId}` and a `variables` object.
4. Refresh the inbox or instance detail after each completion.

Instance-addressed actions are intentionally ambiguous when several child items
are active and return `409`.

## Early quorum and flow evidence

Example 04 uses `completionEvaluation: "afterEach"`, so aggregate conditions
are evaluated after every item action. Its conditions demonstrate:

- `CountFlow(id)` and `PercentFlow(id)`, scoped to this multi-instance execution.
- `[mi.total]`, `[mi.completed]`, and `[mi.remaining]`.
- `FlowInfo(id, 'actions.count')`, the instance-lifetime action summary, including
  evidence staged by the current action in the same transaction.
- Unique positive `completionPriority` values, where the lowest matching value
  wins.

For a visible priority test, submit at least one `Object` before the third
`Approve`. When the approval quorum arrives, flows `201` and `202` both match;
flow `202` has priority `1`, so the instance rests at **Resolve Mixed Quorum**.
Without a prior objection, flow `201` ends the instance as approved as soon as
the third approval is committed.

## Parent interrupt

The manager action in example 05 is a parent-level interrupt, not a child vote.
Discover and take it with:

```text
GET  /api/multi-instance-executions/{executionId}/flows
POST /api/multi-instance-executions/{executionId}/flows/202
```

Use this request body for the POST:

```json
{
  "variables": {
    "interruptReason": "Policy changed during review"
  }
}
```

The caller must satisfy both the node role and flow role (`ReviewManager`) but
does not need an assigned child item or claim. The locked parent advances only
once; later or concurrent losing attempts return `409`. The engine cancels all
unfinished children, writes the index-ordered child rows, and appends one final
`kind: "parentInterrupt"` row containing flow `202`, the manager, action-time
roles, timestamp, and `interruptReason`.

## Worker boundary

Examples 01 through 05 are synchronous and require only `Flowbit.Api`. Example
06 authors `asyncBefore`, `asyncAfter`, and a recurring non-interrupting timer
boundary, so run `Flowbit.Worker` as well. Its 15-second local timings are for
demonstration; increase them before adapting the definition for production.
