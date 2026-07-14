# Multi-Instance API Tests

This console runner creates uniquely named workflow definitions through the live
Workflow Engine API and verifies parallel and sequential multi-instance runtime
behavior against PostgreSQL. It does not update production API code and it does
not delete the workflows or instances it creates.

## Prerequisites

- .NET 10 SDK.
- The PostgreSQL connection configured by
  `src/WorkflowEngine.Api/appsettings.json` is reachable.
- `WorkflowEngine.Api` is running (default: `http://localhost:5017`).
- Development JWT issuer, audience, and signing key match the API settings.

From the `WorkflowEngine` directory:

```powershell
dotnet run --project .\src\WorkflowEngine.Api\WorkflowEngine.Api.csproj --launch-profile http
dotnet run --project .\tools\MultiInstanceApiTests\MultiInstanceApiTests.csproj
```

For the restart-recovery case, let the runner own the API process:

```powershell
dotnet run --project .\tools\MultiInstanceApiTests\MultiInstanceApiTests.csproj -- --manage-api
```

Optional arguments:

```text
--api <url>          API base URL (default: http://localhost:5017)
--jwt-key <key>      Development JWT signing key
--fixture-root <dir> Directory containing the three vote fixture JSON files
--report-dir <dir>   Evidence output directory (default: <fixture-root>/TestResults)
--run-id <id>        Stable identifier used in workflow keys and report names
--manage-api         Start/restart/stop WorkflowEngine.Api for recovery testing
--api-project <path> Override the API project used by managed mode
```

The process exits `0` only when every case passes. It writes both Markdown and
JSON reports named `multi-instance-api-<run-id>.*`. Authorization headers and
JWTs are never written to the reports.

## Cases

| ID | Behavior |
| --- | --- |
| ENV-001 | API/OpenAPI readiness |
| DEF-001 | Create and publish parallel collection, sequential collection, cardinality, and empty-collection definitions |
| DEF-002 | Reject unsupported `completionEvaluation` |
| VAL-001 | Empty collection fails atomically while entering the multi-instance node |
| PAR-001 | Parallel initialization, assignment/inbox isolation, action visibility, validation, and ambiguous legacy action |
| PAR-002 | `afterEach` early quorum, cancellation, result ordering, flow counts, and history |
| PAR-003 | Five-way completion race closes once and returns stale-action conflicts |
| PAR-004 | Interrupt authorization/race, cancellation, and fresh loop re-entry |
| SEQ-001 | Pending guard, one-at-a-time activation, stale replay, ordered completion/results/history |
| SEQ-002 | Sequential interrupt cancels active and pending remainder atomically |
| CAR-001 | `onePerActor` race and `afterAll` approval routing |
| CAR-002 | `afterAll` reject routing |
| CAR-003 | Engine-only default fallback to the faulted end event |
| DEF-003 | Create extended sequential, priority, claim, context, cancellation, duplicate, and load definitions |
| AUTH-001 | Missing, malformed, expired, role-less, and non-admin authorization boundaries |
| COL-001 | Null, empty, numeric, and object collection members are rejected atomically |
| COL-002 | Duplicate case-variant usernames remain distinct collection work items |
| PAR-005 | All parallel items complete without a condition and use the engine-only default |
| RACE-001 | Quorum completion races a parent interrupt; one route wins exactly once |
| RACE-002 | Quorum completion races instance cancellation; terminal state remains consistent |
| SEQ-003 | Sequential early quorum cancels active/pending remainder |
| SEQ-004 | Sequential `afterAll` does not route early |
| OUT-001 | Multiple true aggregate conditions use the lowest completion priority |
| CAR-004 | Zero, negative, decimal, missing, maximum, and over-maximum cardinality |
| CAR-005 | `onePerActor` with concurrent claim, unclaim, and case-insensitive duplicate protection |
| CTX-001 | `mi.*`/`mi.item` flow context and typed/validated submitted-variable rollback |
| REC-001 | Active parallel and sequential executions survive a real API restart |
| LOAD-001 | 1,000 active items, inbox pagination/filtering, and bounded concurrent completion |

The fixture `votes-cardinality.json` is intentionally not used: its cardinality
expression references a value that its start event does not declare. The valid
`votes-cardinality-approve-reject.json` fixture covers cardinality behavior.
