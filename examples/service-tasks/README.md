# Service task examples

These definitions use the implemented `rest` service connector. They reference
a deployment-controlled mock endpoint and token; the repository contains no
secret and no public URL. Definition and editor tests only parse and validate
the JSON—they do not invoke the endpoint.

## Catalog

| File | Start values | Actor and actions | Expected result | Runtime requirements |
| --- | --- | --- | --- | --- |
| [`01-rest-templating-and-typed-output.json`](01-rest-templating-and-typed-output.json) | Required `requestId` and `customerId`; optional `locale`, `quantity`, `urgent`, and `requestTags` have defaults. Start as `ExampleRequester`. | `ExampleReviewer` takes **Complete review** after the call succeeds. | URL/header/body placeholders are rendered; a 2xx mock response atomically writes the status plus declared and dynamic typed outputs. | API and database, a local/private mock API, and the two trusted configuration values below. No Worker. |
| [`02-rest-error-boundary-and-error-end.json`](02-rest-error-boundary-and-error-end.json) | Required `requestId` and `resourceId`. Start as `ExampleRequester`. | `ExampleOperator` supplies `retryReason` and takes **Retry REST call**, or takes **End with domain fault** with the optional `faultNote`. | A non-2xx/timeout/network failure writes `serviceStatus`, captures `serviceError`, and rests at the operator task. The terminal action faults the instance with `EXAMPLE.REST_FAILURE`. | API and database, a local/private failing mock route, and the two trusted configuration values below. No Worker. |

## Required trusted configuration

Resolve these references through Flowbit's trusted configuration context:

| Reference | Example local value | Purpose |
| --- | --- | --- |
| `${config.exampleApiBaseUrl}` | `http://127.0.0.1:5099` | Base address of a developer-controlled mock server. |
| `${config.exampleApiToken}` | A local test token supplied outside the definition. | Value inserted after the `Bearer` scheme in the request header. |

Do not replace the token reference with a committed credential. For local
testing, point the base URL only at a mock service you control. The authored
timeouts are deliberately short and the connector performs no retries; retry in
the second definition is an explicit operator action.

## Success mock contract

For `01-rest-templating-and-typed-output.json`, a start such as:

```json
{
  "requestId": "req-example-001",
  "customerId": "customer-42",
  "quantity": 3,
  "urgent": true,
  "requestTags": ["catalog-example", "priority"]
}
```

produces a request equivalent to:

```http
POST /orders/customer-42
Authorization: Bearer <trusted-config-token>
Content-Type: application/json
X-Request-Id: req-example-001
X-Locale: en-SA

{"customerId":"customer-42","quantity":3,"urgent":true,"tags":["catalog-example","priority"],"note":"request req-example-001"}
```

Have the mock return HTTP 201 and this JSON shape:

```json
{
  "data": {
    "order": { "id": "order-1001" },
    "accepted": true,
    "total": 149.5,
    "labels": ["new", "priority"],
    "shipDate": "2026-08-05",
    "receivedAt": "2026-08-01T12:30:00Z"
  },
  "meta": {
    "traceId": "trace-example-001"
  }
}
```

`httpStatus`, `orderId`, `accepted`, `total`, `labels`, `shipDate`,
`receivedAt`, and `responsePayload` match declared process-variable contracts.
`responseTrace`, `warnings`, and `reviewRequired` demonstrate dynamic typed
outputs; the last two use authored defaults when their paths are absent. No
output mapping is persisted unless the complete batch resolves, type-checks,
and passes all mapping and process-variable validations.

## Failure mock contract

For `02-rest-error-boundary-and-error-end.json`, start with values such as:

```json
{
  "requestId": "req-failure-001",
  "resourceId": "unavailable-42"
}
```

Configure the mock route `GET /failures/unavailable-42` to return, for example:

```http
HTTP/1.1 503 Service Unavailable
Content-Type: application/json

{"error":{"code":"DEPENDENCY_UNAVAILABLE","retryable":true}}
```

The attached boundary catches the failure. `serviceStatus` becomes `503`, and
`serviceError` receives the engine diagnostic. Reconfigure the mock to
return 2xx before choosing **Retry REST call** to reach **Unexpected REST
Success**, or leave it failing and choose **End with domain fault** to finish
with instance status `Faulted` and fault code `EXAMPLE.REST_FAILURE`.
