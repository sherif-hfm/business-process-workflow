# Script task examples

These definitions show both script authoring modes and the instance-wide
`FlowInfo` API. They contain no network calls and need no trusted configuration.
Load a file into `flowbit-editor.html` for inspection, or publish it through the
API to execute it.

## Catalog

| File | Start values | Actor and actions | Expected result | Runtime requirements |
| --- | --- | --- | --- | --- |
| [`01-ncalc-helper-functions.json`](01-ncalc-helper-functions.json) | None; process defaults provide the input text. | `ExampleReviewer` takes **Finish inspection**. | The ordered assignments exercise every custom text helper plus representative NCalc built-ins. The instance rests at **Inspect Helper Results** before completion. | API and database; no Worker or configuration. |
| [`02-javascript-execution-api.json`](02-javascript-execution-api.json) | None; process defaults provide typed bridge values. | `ExampleReviewer` takes **Finish inspection**. | JavaScript reads one/all variables, tests existence, and writes string, number, boolean, array, date, datetime, and nested JSON values. | API and database with the configured Jint sandbox; no Worker or external configuration. |
| [`03-flowinfo-ncalc-and-javascript.json`](03-flowinfo-ncalc-and-javascript.json) | None. | `ExampleReviewer` selects **Accept** or **Return**. | NCalc and JavaScript snapshot the selected flow's action/traversal evidence. The Exclusive gateway reaches the matching terminal event. | API and database; no Worker or configuration. |
| [`04-javascript-error-boundary.json`](04-javascript-error-boundary.json) | None. | `ExampleOperator` takes **Acknowledge handled failure** after the boundary catches the error. | The script fails deliberately. `stagedValue` remains `unchanged`, `scriptError` captures the diagnostic, and the instance rests at **Inspect Caught Error**. | API and database; no Worker or configuration. |

## Expected helper values

`01-ncalc-helper-functions.json` starts with `rawText` equal to
`"  Flowbit Examples  "`. After the NCalc task:

- `normalizedText` is `Flowbit Examples`;
- `lengthValue` and `lenAliasValue` are `16`;
- `emptyCheck` is `false`, while `whitespaceCheck` is `true`;
- the case-insensitive contains, prefix, suffix, and regex checks are `true`;
- `upperText` and `lowerText` contain the corresponding case conversions;
- `boundedLength` is `16`, `sizeLabel` is `long`, `knownLabel` is `true`, and
  `mathResult` is `12`.

The ordering is intentional: later assignments read values written earlier in
the same script task.

## JavaScript execution bridge

`02-javascript-execution-api.json` demonstrates the complete bound API:

```javascript
execution.getVariable("name")
execution.setVariable("name", value)
execution.getVariables()
execution.hasVariable("name")
```

Only declared process variables may be written. Values crossing the bridge must
be JSON-compatible and are coerced to each declaration's `dataType` and
`isArray` contract. The sandbox has no CLR, filesystem, reflection, or network
access; it also applies the deployment's statement, memory, recursion, timeout,
regex, array, and JSON-bridge limits.

## Flow evidence

In `03-flowinfo-ncalc-and-javascript.json`, flows `201` and `202` are explicit
user actions. The following NCalc shape reads a scalar evidence property:

```text
FlowInfo(201, 'actions.count')
FlowInfo(201, 'traversals.count')
```

The JavaScript task opts in with `usesFlowInfo: true` and reads the complete
summary with `execution.getFlowInfo(201)`. A summary contains `actions` and
`traversals`, each with a lifetime `count` and nullable `last` snapshot. The
downstream Exclusive gateway proves that evidence staged by the selected action
is visible later in the same transaction.

## Error-boundary behavior

`04-javascript-error-boundary.json` first stages a write and then throws an
intentional JavaScript error. Script writes are committed atomically, so the
staged write is discarded. The attached `errorBoundaryEvent` records the
failure in `scriptError` and follows its one unconditional error path. The
normal outgoing path exists for diagram completeness but is not reached by this
example.
