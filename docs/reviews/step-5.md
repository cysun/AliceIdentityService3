# Step 5 review

Step 5 was reviewed and committed as `6f1db91`. This checklist records its implementation and verification. Current work and the next review gate are in the [Step 6 review](step-6.md).

## Implemented behavior

- Functional registration in the approved full and compact Razor layouts: first/last name, email, password, and confirmation. Validation preserves safe fields/context and clears both password fields. New accounts have lockout enabled, remain unconfirmed, and receive no administrator claim. Existing Identity password requirements are unchanged.
- Duplicate registration preserves the existing profile and password. A matching unconfirmed address can receive another confirmation message; confirmed, unknown, and ambiguous legacy addresses receive the same normal status page without mail. Concurrent new registrations resolve to one username/account.
- Purpose-specific Identity/Data Protection confirmation tokens expire after 24 hours. GET displays the confirmation form without changing the account; POST requires antiforgery and commits confirmation plus security-stamp rotation together. Used, tampered, expired, wrong-user, or old-stamp links fail. Resend does not invalidate earlier unexpired links; successfully confirming once invalidates all outstanding links for that account.
- Confirmation does not create a login session. The user signs in with the password afterward. A valid ten-minute OIDC context is preserved through registration, email, confirmation, and login; the authorization endpoint validates the request again. If context expires before the email is used, confirmation still succeeds and the page explains how to restart sign-in from the application. Opening email in another browser may require restarting the client flow because the client's correlation/PKCE session belongs to the original browser.
- Registration and resend share an IP limit of ten POSTs per fifteen minutes (429 with Retry-After). Recipients share a cooldown of one minute and a cap of five email attempts per hour, including failed attempts. Recipient keys are hashed and stored in a bounded process-local cache. Limits reset on restart; distributed abuse controls remain a deployment review item.
- Confirmation content lives in `EmailTemplates/EmailVerification.Subject.txt`, `.Body.txt` (plain text), and `.Body.html`. The two bodies use the literal `{{link}}` placeholder, with HTML escaping applied by the renderer. Subjects must be one nonempty line and both bodies must contain the link. Files are read on each send and included in build/publish output; missing/invalid templates fail before publication.
- `IAccountEmailSender` produces text/HTML MIME confirmation messages and publishes the existing AliceMailService MessagePack `List<byte[]>` contract. A singleton RabbitMQ publisher lazily opens/reuses a connection, uses a separate channel per publication, declares a durable queue, sends persistent/mandatory messages, and awaits publisher confirmation. It can retry connection establishment on a subsequent user request after an initial failure. No connection is opened at normal web startup.
- A publication/configuration failure returns a 503 retry page. The new account stays unconfirmed, and resend can recover after the cooldown. Application mail diagnostics contain failure types only, without recipients, links, tokens, or raw exception text. Confirmation URLs use configured `Email:AppUrl`, never an untrusted request Host header.

The architecture remains one ASP.NET Core backend, Razor account pages, and the React/Base UI admin SPA. Only the admin preview step label changed. Recovery/profile work is not implemented. No schema, migration, SQL, private configuration, or AliceMailService source changes were needed. `CreateSchema.sql` and `PopulateSchema.sql` are unchanged, private appsettings and migration code remain ignored, and the sample settings remain tracked.

## Mail configuration

Copy the **Email** and **RabbitMQ** sections from `AliceIdentityService/appsettings.sample.json` into private appsettings.json, preserving your existing database, certificate, and logging settings. Alternatively, use environment variables. The relevant settings are:

| Setting | Meaning |
| --- | --- |
| `Email:AppUrl` | Public HTTPS origin, such as `https://localhost:7443/`; no path/query/user information. Use the same externally reachable origin as the OIDC issuer for local review. |
| `Email:SenderName`, `Email:SenderEmail` | From identity accepted by your SMTP provider. Replace the sample address before real mailbox testing. |
| `RabbitMQ:HostName`, `Port`, `VirtualHost` | Broker connection; defaults localhost, 5672, `/`. |
| `RabbitMQ:UserName`, `Password`, `QueueName` | Credentials and destination queue. The consumer must be configured for the same broker/queue. Defaults match AIS2's local setup. |
| `RabbitMQ:UseTls`, `TlsServerName` | Optional publisher TLS with normal certificate/hostname validation. An empty TLS server name uses HostName. Configure the port and consumer connection appropriately. |
| `RabbitMQ:PublishTimeoutSeconds` | Bounded publication deadline, including waiting for the publisher; default 10 seconds. |

SMTP host, credentials, sender permissions, and delivery behavior belong to **AliceMailService**. AIS3 sends no SMTP directly. For review, use a dedicated queue (for example `ais3-step5-review`) and a test AliceMailService instance configured to consume it and deliver to your test mailbox or local SMTP capture server. Avoid attaching a test consumer to a live queue.

If your existing private AIS3 configuration has no Email section, login/OIDC still run; registration records an unconfirmed account but displays the retry page until a sender is configured. An unavailable consumer differs from an unavailable broker: the broker can accept and retain mail while the consumer is stopped. The success page reports that mail should arrive; it does not claim final delivery.

## Manual review

1. Point AIS3 at a **test database**, either initialized using the [database guide](../database.md) or restored from AIS2. Configure mail as above. Start RabbitMQ and the configured test AliceMailService consumer before registration. Keep private configuration and credentials out of version control.
2. From the AIS3 repository root, build the admin assets and run the backend, or launch the same backend in Rider:

   ```sh
   npm run build --prefix AliceIdentityService.Admin
   dotnet run --project AliceIdentityService
   ```

   The existing `npm run dev --prefix AliceIdentityService.Admin` workflow also works. Run only one backend workflow on port 7443. Trust the local HTTPS certificate if needed using `dotnet dev-certs https --trust`.

3. In a private browser window, open `https://localhost:7443/Account/Register`. Try a mismatched/weak password, then register using a test mailbox you control. Expect the approved layout, validation messages, empty password fields after errors, and a check-email page after success. Attempt sign-in before confirmation: it must fail with the existing generic login message.
4. Open the received email. It contains the configured HTTPS confirmation link and a 24-hour expiry notice. Opening the link displays **Confirm email**; submit that button. Expect **Email confirmed**, then **Continue to sign in**. Sign in with the registered password. The account has no administration access.
5. Reopen the used link: expect the invalid/expired/used-link page. Register again with the same address and different profile/password values: the original account remains unchanged. Sign-in still requires its original password.
6. For another unconfirmed test account, use **Resend confirmation email** from login or the check-email page. Wait at least one minute between requests. Requests for unknown or already-confirmed addresses show the generic result without sending mail. Automated tests cover the hourly/IP caps without repeatedly contacting a mailbox.
7. For a safe publication-failure check, temporarily point the **test AIS3 instance** at an unused broker port (for example `RabbitMQ__Port=1`), then register a new test address. Expect the retry page and an unconfirmed account. Restore the correct port/restart, then use resend to finish. Do not stop a shared broker to perform this check. A missing consumer alone should leave mail queued, not cause this failure page.
8. For OIDC registration, register/run the existing review client:

   ```sh
   dotnet run --project AliceIdentityService -- review-client
   # Keep the AIS3 backend running in its own terminal, then:
   dotnet run --project tools/OidcReviewClient
   ```

   Open `https://localhost:9443/` while signed out of AIS3, choose **Sign in with AIS3**, then **Create an account**. Use another test email. Expect the compact layout naming **AIS3 Review Client** throughout. Open the confirmation link in the same browser within ten minutes, confirm, and sign in: the client receives the new user's claims with `email_verified=true`. If the sign-in context/correlation expires, confirmation still works; restart sign-in from the client. Automated tests cover expired context without waiting.
9. Stop every server you started with Ctrl+C or Rider Stop, including a temporary mail consumer/capture server. Leave shared PostgreSQL/RabbitMQ services running if they were already running.

## Reviewing template edits

Edit the three files under `AliceIdentityService/EmailTemplates`, then request a confirmation email using the manual flow above. Preserve `{{link}}` in both bodies and keep the subject to one nonempty line. The renderer supports that placeholder only; it does not execute template expressions. With `dotnet run`, edits are picked up from the backend content root on the next send. Published deployments include the same files under `EmailTemplates`. Password-reset templates will be added in Step 6.

For the focused rendering/packaging checks:

```sh
AIS3_TEST_RABBITMQ_HOST=localhost dotnet test --filter FullyQualifiedName~MailTests
dotnet publish AliceIdentityService -c Release -o artifacts/template-review
# Check artifacts/template-review/EmailTemplates for all three files.
```

The RabbitMQ test uses only its isolated queue, as described below.

## Automated checks

From the AIS3 repository root:

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
dotnet list AliceIdentityService/AliceIdentityService.csproj package --vulnerable --include-transitive
git diff --check
```

Adjust database host/user/name. The PostgreSQL admin connection creates/drops disposable databases; the restore connection points to a separate AIS2 dump restore and its check is read-only. The RabbitMQ test uses port 5672 and `/`, creates a random isolated queue, consumes its own messages, and deletes that queue. It does not send SMTP email or touch the configured application queue. Optional `AIS3_TEST_RABBITMQ_USERNAME`/`AIS3_TEST_RABBITMQ_PASSWORD` override local guest credentials. The respective integration tests skip when their enabling environment variables are absent.

The new tests cover full registration/confirmation/login, validation and CSRF, duplicate/racing requests, publication failure/resend recovery, used/expired/tampered/stamp-invalid links, OIDC completion and expired context, throttling, MIME/MessagePack compatibility, URL configuration, persistent broker-confirmed publication, and recovery from an initial broker connection failure. Existing account/OIDC/admin/schema tests remain part of the suite.

## Verification — 2026-11-15

- All 64 backend tests passed with PostgreSQL, a separate read-only AIS2 restore, and isolated RabbitMQ queues enabled, including 15 new registration/mail cases. C# formatting verification passed. Fresh databases used the unchanged versioned SQL scripts without migration sources or private configuration.
- All 17 frontend tests passed, along with formatting, lint, and the React build. The AIS3 package audit reported no known vulnerable direct or transitive packages.
- A browser completed compact OIDC registration, received a real message through RabbitMQ → the existing AliceMailService code → a loopback SMTP capture server, submitted confirmation, then signed in and returned to the OIDC review client with the correct user and `email_verified=true`. No external email was sent. The approved layout rendered correctly.
- The separately built AliceMailService verification copy reported existing `System.Security.Cryptography.Pkcs` 10.0.0 advisories ([moderate](https://github.com/advisories/GHSA-2fmv-rv2c-59x7), [high](https://github.com/advisories/GHSA-846f-f764-76mx)). AIS3's new package graph did not report these. AliceMailService source/dependencies were left unchanged; review that service's dependency update separately.

- Template review adjustment: all seven focused mail tests passed, including editable template content, HTML escaping, invalid/missing template failure, web-content-root resolution, and RabbitMQ publication. Build/publish output includes all three templates; private configuration remains excluded.
- Verification servers were stopped and temporary databases, queues, and captured messages removed before handoff. The configured AIS3/AIS2 databases and shared RabbitMQ service were left unchanged. Step 5 remains uncommitted; Step 6 has not started.

## Limits retained for later steps

Step 5 implements broker acceptance and immediate publication-failure handling, not guaranteed mailbox delivery or exactly-once messaging. A timeout can occur after acceptance; a later resend may duplicate an email. There is no transactional outbox or automatic retry in AIS3. Step 12 retains the review of delivery failures, retry/recovery operations, monitoring, and deployment verification. Production remains gated, and Data Protection storage plus distributed limits still require staging review. Step 6 will reuse the sender for recovery; it has not started.
