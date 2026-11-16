# Step 12 review — RabbitMQ mail publication

Steps 1–12 are reviewed and committed; Step 12 is `954b5bd`. This checklist records the Step 12 handoff. Current work is documented in the [Step 13 review](step-13.md).

## Scope and behavior

AIS3's mail integration was implemented in Steps 5 and 6. It generates account links, renders templates, serializes MIME messages into the agreed MessagePack `List<byte[]>` contract, and publishes to RabbitMQ. Step 12 reviews that publication boundary and adds a small logging improvement and focused regression tests.

The existing publisher declares a durable queue, uses persistent/mandatory publication with publisher confirmations, bounds the attempt with a timeout, reuses its connection and creates a channel per request. Initial connection failure can recover on a later request; a failed channel does not prevent a subsequent request from opening another channel. **No active consumer is required for successful publication.** RabbitMQ can retain the message until AliceMailService is available.

The retained change is safe publication logging: successful broker confirmation records the mail kind and generated MIME Message-ID; failure records kind, stage (configuration/template/message/broker) and exception type; caller cancellation is logged and propagated. Recipients, names, message bodies, action links, tokens and raw exception messages are excluded. The failure exception says publication was not confirmed, since a timeout or interrupted connection may leave broker acceptance uncertain. AIS3 never automatically replays that attempt.

SMTP delivery, delivery retries, failed queues and consumer operations belong to AliceMailService. AIS3 adds no Mail page/API, queue diagnostics, `mail-check` command, metrics subsystem, SMTP capture harness or consumer dependency. There is no consumer-specific queue-name restriction. Broker configuration, the publisher implementation and the account flows remain as approved in Steps 5/6.

The approved Razor and React/Base UI layouts remain; only the preview step label changes. No database schema/model/SQL, migration, dependency, private settings or sample settings changes are needed. CreateSchema.sql and PopulateSchema.sql remain unchanged; private appsettings.json and migration code remain ignored; appsettings.sample.json stays tracked.

## Testing instructions

The focused tests need only a test RabbitMQ broker; they create random isolated queues, read only their own synthetic messages and delete their queues afterward. They do not start AliceMailService, send SMTP mail or use the configured application queue.

```sh
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test --filter FullyQualifiedName~MailTests
```

The test broker uses port 5672/vhost `/`; optional `AIS3_TEST_RABBITMQ_USERNAME` and `AIS3_TEST_RABBITMQ_PASSWORD` override local guest credentials. Without the host variable, RabbitMQ tests skip. The tests verify:

- Existing MIME/MessagePack compatibility, configured HTTPS link origin, edited templates, persistent publication and recovery from an initial connection failure.
- Successful broker-confirmed publication with zero consumers, leaving exactly one ready message.
- A queue-declaration conflict rejects the current publication; after fixing the test queue, the same publisher succeeds on a subsequent request using a fresh channel.
- Successful publication logs its MIME Message-ID. Failed/cancelled attempts are not logged as successful, are not automatically replayed, and do not expose private mail data.

For manual account checks, use a disposable database, a dedicated test queue and your existing private `Email`/`RabbitMQ` settings. The [Step 5 checklist](step-5.md#mail-configuration) documents the publisher configuration. Keep a test consumer disconnected to check queueing alone; do not stop or alter a shared live consumer.

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

1. Confirm administration retains its existing navigation with no Mail page. Existing account, client and scope workflows remain available.
2. Register a synthetic test address against the isolated queue, or request confirmation for an eligible test account. With RabbitMQ available and no consumer, the normal check-email response should succeed and the queue should hold the message. A broker-confirmed log should appear; no SMTP delivery is implied.
3. Restart only the test AIS3 instance with `RabbitMQ__Port=1`, then use another test registration or an eligible resend after its cooldown. Expect the existing 503 retry page and an unconfirmed account; the log should identify a broker publication failure without the recipient or action link. Password recovery retains its generic response even when publication fails.
4. Restore the correct port, restart the test instance and request fresh mail after the cooldown. The publication should succeed. Failed attempts continue counting toward the existing limits; do not bypass them. A timeout can leave a duplicate message possible, so do not introduce blind automatic retries.
5. Inspect logs: no recipient address/name, content, token, action URL or raw exception text. Use the isolated broker test above for deterministic channel-failure and cancellation checks rather than disrupting shared infrastructure.
6. Remove only your disposable queue/messages and stop verification servers. Review the uncommitted changes before authorizing Step 13.

Full regression commands, from the repository root:

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
git diff --check
```

Adjust connection values. The admin connection creates/drops temporary databases; the restore must be a separate AIS2 copy with PopulateSchema.sql applied. Build the frontend before backend HTTP tests. Integration tests skip when their enabling variables are absent. No consumer path or SMTP settings are needed.

## Verification

Verified for this handoff: **123 backend tests passed, none skipped; 70 frontend tests passed**. The focused mail run passed all 10 tests with RabbitMQ enabled. Frontend formatting, lint, type checking and production build passed, as did .NET formatting, Release publish and `git diff --check`.

The published output contains all six email template files and no private appsettings.json, migration source or Mail administration bundle. PostgreSQL checks included a disposable AIS2 restore with PopulateSchema.sql applied; the restore and temporary dump were removed. RabbitMQ test queues were removed. No AliceMailService consumer or SMTP server was needed. Verification server ports 7443 and 9443 have no listeners.

At that handoff, changes were uncommitted and Step 13 had not started; see the current review linked above.
