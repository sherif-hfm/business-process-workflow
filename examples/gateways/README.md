# Gateway examples

These definitions are small, valid Flowbit workflows designed to make one
gateway behavior easy to observe at a time. Load any JSON file directly into
`flowbit-editor.html`, or publish it and run an instance through the API/UI.

## Examples

| File | What it demonstrates |
| --- | --- |
| `01-exclusive-priority-and-default.json` | An Exclusive split evaluates non-default routes by ascending `conditionPriority` and selects only the first match. The separate Exclusive merge has multiple incoming flows and one unconditional outgoing flow. |
| `02-parallel-fork-and-join.json` | A Parallel split creates Finance, Legal, and Security work simultaneously. The Parallel join waits for one token from every incoming branch. |
| `03-inclusive-conditional-split-and-merge.json` | An Inclusive split selects every true condition. Its default is used only when no condition matches, and the merge waits only for branches selected by the active split. |
| `04-complex-two-of-three-merge.json` | A Parallel fork creates three reviews, while a Complex merge activates when `TotalIncomingCount() >= 2`. A terminate end event closes any remaining review after the coordinator finalizes. |
| `05-complex-start-reset-cycle.json` | A Complex split uses `IncomingCount(flowId)` for activation and `[gateway.waitingForStart]` to choose start-phase versus reset-phase work across cycles. |
| `06-scoped-interrupt-parallel-branch.json` | Flowbit's `scopedInterruptEvent` references a Parallel split with `gatewayRef`; triggering it cancels sibling work in the nearest active activation and continues on an emergency path. |

## Suggested inputs and actions

### Exclusive routing

Start `01-exclusive-priority-and-default.json` with:

- `urgent=true` and any amount to select **Expedited Review**, because priority
  1 is evaluated before the amount rule.
- `urgent=false` and `amount=25000` to select **Manager Approval**.
- `urgent=false` and `amount=500` to take **Standard Processing**, the default.

### Inclusive routing

Start `03-inclusive-conditional-split-and-merge.json` with:

- both flags `true` to create Legal and Security work and make the merge wait
  for both;
- exactly one flag `true` to create and wait for only that branch;
- both flags `false` to create only the default Lightweight Review branch.

### Complex quorum merge

In `04-complex-two-of-three-merge.json`, complete any two reviewer tasks. The
Complex merge activates and creates **Finalize Quorum Decision**. Taking its
action reaches a terminate end event, which cancels the third review if it is
still active.

### Complex phases

In `05-complex-start-reset-cycle.json`, take **Demonstrate reset phase** from the
first Start-Phase task. The next activation routes to Reset-Phase work. Take
**Begin the next cycle** to observe the gateway return to Start-Phase work with
an incremented cycle. Either phase also offers a finish action.

### Scoped interrupt

In `06-scoped-interrupt-parallel-branch.json`, leave **Normal Investigation**
active and take **Trigger scoped interrupt** from **Raise Emergency**. The
investigation task is cancelled and the promoted token rests at **Emergency
Response**.

## Gateway authoring rules illustrated here

- Gateway direction is inferred from topology. A split has exactly one incoming
  and at least two outgoing flows; a merge has at least two incoming and exactly
  one outgoing flow.
- Exclusive and Inclusive splits require exactly one unconditional default.
- Exclusive non-default flows require unique positive priorities; Inclusive
  flows do not use priorities.
- Parallel flows are unconditional.
- Complex gateways require an `activationCondition`; their non-default outgoing
  flows also require conditions.
- A scoped interrupt must be structurally reachable from the referenced
  Parallel, Inclusive, or Complex split.
