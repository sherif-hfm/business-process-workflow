# Flowbit durable async and timer examples

These definitions are intentionally small and use short delays so you can load
them into `flowbit-editor.html`, inspect the authored controls, publish them to
the API, and watch `Flowbit.Worker` process their jobs and timers.

## Examples

1. `01-async-before-after.json`
   - `asyncBefore` queues the script before it executes.
   - The script increments `executionCount`.
   - `asyncAfter` on the next automatic task commits its completion, then queues
     outgoing routing.
   - Its explicit empty retry list demonstrates disabling automatic retries.

2. `02-intermediate-timer-delay.json`
   - The instance waits for ten seconds at an intermediate timer catch event.
   - The token remains active while it waits and then moves to `Timer Fired`.

3. `03-recurring-timer-start.json`
   - Creates one new workflow instance every 30 seconds, up to five occurrences.
   - Timer starts are scheduled only for the published default workflow version.

4. `04-absolute-timer-start.json`
   - Demonstrates an absolute timer start authored as `2030-01-01T09:00:00Z`.
   - Use the editor's local date/time picker to replace it with a nearer future
     instant before testing.

5. `05-user-task-reminder-and-deadline.json`
   - A non-interrupting boundary fires three reminders at 15-second intervals.
   - The original `Approve Request` user task remains active while reminders run.
   - A separate interrupting one-minute boundary cancels the original task and
     creates `Escalated Approval`.

6. `06-multi-instance-reminder.json`
   - `asyncBefore` and `asyncAfter` wrap the parent multi-instance body.
   - Three parallel user-task items are created.
   - A non-interrupting recurring boundary records reminders while unfinished
     child items and the parent token remain active.

## Quick test procedure

1. Open `flowbit-editor.html` and choose **Load JSON**.
2. Select one of the example files and inspect its Durable async or Timer panel.
3. Save or publish the definition through your normal API/UI workflow.
4. Start both `Flowbit.Api` and `Flowbit.Worker`.
5. For timer-start examples, publish the version as the workflow's default.
6. Start a normal instance for the other examples and watch Jobs, Incidents,
   instance history, variables, and active user tasks in the operations UI.

The durations are for local testing. Increase them before using these examples
as the basis for a production workflow.
