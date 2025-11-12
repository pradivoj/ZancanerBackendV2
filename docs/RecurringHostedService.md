RecurringHostedService

Overview

- Background worker (derived from `BackgroundService`) that runs periodically and forwards pending orders from the local database to an external API.
- Designed to be resilient and non-blocking: it writes application logs via `ILogger` and business/audit logs to the database using `IDbLogService`.

Behavior

- On each loop iteration (interval configured) the service:
  - Writes a `RECURRING_SERVICE_START` entry to the DB bitácora with a process correlation id.
  - Calls the stored procedure configured by `RecurringService:ProcedureName` (default: `CSP_ZANCANER_ORDERS_SENT_DATA_TO_ZANCANCER_API`) to read orders to send.
  - Processes the result set row-by-row. For each row:
    - Extracts a `CorrelationId` column (if provided) and uses it as `messageId` in the outgoing payload. If absent, generates a new GUID.
    - Builds a single-record payload JSON with `messageType: "CREATE_ORDER"`, `messageId` and `records: [ { ... } ]` where the record contains the row fields (keys converted to camelCase; `productionOrder` normalized to string).
    - Sends the payload via HTTP POST to the configured external endpoint (`RecurringService:ExternalEndpoint`).
    - Writes a `RECURRING_SERVICE_SEND` entry into the DB bitácora for each order, using the row's correlation id as the bitácora correlation id and storing `Production_Order=<order>` in params.
    - Calls the local stored procedure `CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER` with parameters `@NEW_STATUS` and `@ORDER` to mark the order as processed:
      - If HTTP send succeeded => `@NEW_STATUS = 900`.
      - If HTTP send failed => `@NEW_STATUS = 200`.
  - After processing all rows, writes a `RECURRING_SERVICE_END` entry with the processed count and the same process correlation id.

Configuration options (appsettings)

- `RecurringService:IntervalSeconds` (int): loop interval in seconds. Default 60. Minimum enforced by code: 5 seconds.
- `RecurringService:ProcedureName` (string): name of the stored procedure that returns rows to send. Default `CSP_ZANCANER_ORDERS_SENT_DATA_TO_ZANCANCER_API`.
- `RecurringService:ExternalEndpoint` (string): full URL of the external API to POST the payloads. Default `http://93.41.138.207:88/ZncWebApi/ProductionOrder/CreateOrder`.
- `RecurringService:HttpTimeoutSeconds` (int): HTTP client timeout in seconds (default 30).
- `ConnectionStrings:DefaultConnection` (string): database connection string used to read orders and update status.

Stored procedures used

- `CSP_ZANCANER_ORDERS_SENT_DATA_TO_ZANCANCER_API` (read): should return rows to be sent. Required columns expected:
  - `ProductionOrder` (or `productionOrder`) — used as `productionOrder` in the payload and converted to string.
  - `CorrelationId` (optional) — a GUID value to use as messageId / correlation id.
  - Other columns — will be included in the record payload (keys converted to camelCase).

- `CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER` (update): called per order to update local status. Parameters:
  - `@NEW_STATUS` (int)
  - `@ORDER` (int)

Bitácora (DB logging)

- Writes the following `Action` values into the bitácora using `IDbLogService.LogAsync`:
  - `RECURRING_SERVICE_START` — start of a loop iteration. Params contain start time.
  - `RECURRING_SERVICE_SEND` — one entry per order processed. Params contain `Production_Order=<order>` and the correlationId is set to the order `CorrelationId`/`messageId`.
  - `RECURRING_SERVICE_SEND_ERROR` / `RECURRING_SERVICE_SEND_EXCEPTION` — recorded when the POST fails (tool records response or exception in the `errorMsg` field).
  - `RECURRING_SERVICE_END` — end of a loop iteration. Params contain processed count.

HTTP payload example

- Payload format sent to external endpoint:

  {
    "messageType": "CREATE_ORDER",
    "messageId": "<correlation-guid>",
    "records": [ { /* single record from the DB row */ } ]
  }

Notes and recommendations

- Logging: the service uses `ILogger` for application logs and `IDbLogService` for business/audit logs. `IDbLogService` must be registered in DI and should be able to write to a persistent bitácora table.
- DI/scopes: the hosted service is singleton, so the service uses `IServiceScopeFactory` to create scopes and obtain `IDbLogService` for each DB write.
- Retries: currently the service does a single POST attempt per order. Consider adding retry with exponential backoff for transient network failures.
- Persistence for failed sends: consider persisting failed messages to a retry table or queue for later processing.
- Testing: point `RecurringService:ExternalEndpoint` to a mock (e.g., `https://webhook.site`) during tests.

Troubleshooting

- If the app fails at startup with: "Cannot consume scoped service 'IDbLogService' from singleton 'IHostedService'", ensure `RecurringHostedService` constructor does not inject `IDbLogService` directly and instead injects `IServiceScopeFactory`.
- If no orders are found, confirm the read stored procedure returns rows and that the DB connection string is correct.

Contact

- For changes in payload schema or SP contracts, coordinate with the API/DB owners to keep the contract stable.
