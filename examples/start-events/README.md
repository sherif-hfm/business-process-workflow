# Start-event examples

## [01 - Business-key scopes and idempotency](01-business-key-scopes-and-idempotency.json)

This workflow has two user-selectable plain start events. Publish the definition, then call `POST /api/instances` with `workflowKey`, the desired `startEventId`, and `variables`.

### Active-scope route

- Start values: select node `1`; send `caseId` as a string beginning with `ACTIVE-`. `caseSummary` is optional.
- Actor: the authenticated caller needs `ActiveCaseStarter`.
- Idempotency: send a nonblank `Idempotency-Key` header. The engine stores it as the implicit `activeRequestKey` variable.
- Expected result: the instance rests at task `3`. Its business key conflicts only while the owning instance is active; after completion, fault, or cancellation, the same `caseId` may be started again. The transport idempotency key remains permanently reserved for the workflow family.

### Permanent-scope route

- Start values: select node `2`; send `caseId` as a string beginning with `PERM-`. `caseSummary` is optional.
- Actor: the authenticated caller needs `PermanentCaseStarter`.
- Idempotency: send a nonblank `X-Permanent-Request-Id` header. The engine stores it as the implicit `permanentRequestKey` variable.
- Expected result: the instance rests at task `3`. The `all` business-key claim remains reserved after every terminal state, and the transport idempotency key is also permanent.

For either route, a `CaseWorker` takes flow `301` to complete at node `4`; a `CaseManager` may cancel a running instance. A duplicate domain key returns `409` with `business_key_conflict`; a duplicate retry key returns `409` with `idempotency_conflict`. Idempotency is evaluated first when both conflict.

No durable-job worker, external endpoint, or secret configuration is required. The two header values identify independent transport requests, while `caseId` identifies the domain case. Keys are exact, case-sensitive values scoped to the stable workflow key across versions.
