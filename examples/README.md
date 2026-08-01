# Flowbit workflow examples

The examples are grouped by their primary task type or engine feature so the
collection can grow without returning to one flat directory.

## Folder layout

| Folder | Feature demonstrated |
| --- | --- |
| [`gateways/`](gateways/) | Exclusive, Parallel, Inclusive, and Complex gateways, plus Flowbit scoped interrupts. |
| [`durable-jobs/`](durable-jobs/) | Durable `asyncBefore` and `asyncAfter` transaction boundaries and retry policy. |
| [`timers/`](timers/) | Intermediate timers, timer starts, and interrupting/non-interrupting boundary timers. |
| [`multi-instance/`](multi-instance/) | Parallel or sequential user-task fan-out and aggregate completion behavior. |

Some examples combine features. They live under the feature that is most useful
for discovering them; for example, the multi-instance reminder example also
uses a non-interrupting boundary timer and durable jobs.

## Gateway examples

The [`gateways/README.md`](gateways/README.md) guide describes the behavior and
test inputs for every gateway definition:

1. Exclusive gateway priority, default routing, and a separate merge.
2. Parallel fork and synchronization join.
3. Inclusive conditional fan-out and reachability-aware merge.
4. Complex two-of-three activation at a merge.
5. Complex start/reset phases and cycles.
6. A scoped interrupt that cancels sibling work from a Parallel split.

## Durable jobs and timer examples

- `durable-jobs/01-async-before-after.json` queues work before a script task and
  after an automatic task. It also demonstrates explicit retry delays and an
  empty retry list.
- `timers/02-intermediate-timer-delay.json` rests an execution token at an
  intermediate timer catch event.
- `timers/03-recurring-timer-start.json` starts one instance every 30 seconds,
  up to five occurrences.
- `timers/04-absolute-timer-start.json` uses an absolute UTC start timestamp.
  Change it to a near-future value before testing.
- `timers/05-user-task-reminder-and-deadline.json` combines a recurring,
  non-interrupting reminder with an interrupting deadline.
- `multi-instance/06-multi-instance-reminder.json` creates three parallel work
  items and records non-interrupting reminders while the parent execution is
  active.

## Loading and running an example

1. Open `flowbit-editor.html` and choose **Load JSON**.
2. Select a definition under the relevant feature folder.
3. Inspect or edit the nodes, sequence flows, and feature-specific inspector
   fields.
4. Save the definition, or publish it through the API/UI.
5. Start `Flowbit.Api`; also start `Flowbit.Worker` for durable-job and timer
   examples.
6. Start an instance and complete the visible user tasks with an actor carrying
   the roles shown in the definition.

The timer durations and retry delays are intentionally short for local testing.
Increase them before adapting an example for production.
