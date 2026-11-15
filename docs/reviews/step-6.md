# Step 6 review

Steps 1–5 are approved and committed. Step 5 is commit `6f1db91`. Step 6 is **uncommitted** for manual review. Step 7 has not started.

## Implemented behavior

- `/Account/ForgotPassword` is a real server-rendered form in both approved account layouts. Only a unique, confirmed email address on an account with a password receives a reset message. Unknown, unconfirmed, ambiguous legacy, and throttled addresses have the same normal response. A mail-publication failure also uses that response to avoid revealing account existence during an outage; the sender logs only the failure type. Invalid form input still shows validation, and the shared IP cap returns 429.
- Recovery uses the Step 5 RabbitMQ/AliceMailService publisher and the same bounded recipient limit as registration/resend: one email attempt per minute and five per hour. All three endpoints share ten POSTs per IP per fifteen minutes. Failed attempts count; limits are local to the process and reset at restart.
- `ResetPassword.Subject.txt`, `.Body.txt`, and `.Body.html` separate email content from code. Both bodies use the existing `{{link}}` renderer, including HTML escaping. The configured `Email:AppUrl` supplies the origin; request Host headers cannot choose it. Template files are copied to build/publish output and read at send time.
- Reset links use a dedicated Identity/Data Protection provider and purpose, expire after one hour, and are bound to a user and security stamp. GET only displays the form. POST requires antiforgery, validates matching passwords and the existing Identity password policy, and saves the new hash and security stamp together. A used, expired, tampered, wrong-user, wrong-purpose, or old-stamp link is rejected. Concurrent submissions cannot both succeed. Requesting another link does not invalidate earlier valid links; one successful reset invalidates all of them.
- `/Account/ChangePassword` requires authentication and the current password. Wrong current passwords use the existing five-attempt/five-minute lockout policy for lockout-enabled accounts. Validation errors clear password fields. Success rotates the stamp and signs out the current browser. Reset and change require a new sign-in in every AIS browser session and invalidate earlier authorization codes/refresh tokens when exchanged. Neither operation clears lockout nor disables two-factor authentication. AIS3 still fails closed for imported two-factor accounts until a challenge flow exists.
- Already-issued access/ID JWTs and application-local sessions can remain usable until their own expiry. Independently validating APIs do not observe Identity security-stamp changes. This is the existing Step 4 token policy, not an immediate revocation guarantee for every application session.
- Valid OIDC context survives request, reset, validation errors, and the subsequent password sign-in. An expired application continuation does not prevent a valid reset; the result asks the user to restart sign-in from the application. Reset never automatically authenticates the browser.
- `/Account` and `/Account/Profile` display and edit first name, last name, and screen name, following the approved profile mockup. Each is required, trimmed, and limited to 255 characters. Email stays read-only and confirmed. The signed-in subject selects the account; submitted IDs, email, hashes, stamps, or claims are ignored. Rendering escapes entered names. Changes persist without renewing the fixed cookie lifetime. Userinfo and newly issued/refreshed ID tokens use current profile values.

The single ASP.NET Core backend, Razor layouts, React/Base UI admin SPA, and AIS2 mappings remain intact. The admin UI only changes its step label; its data/session integration remains Step 7. No schema, SQL, migrations, dependencies, private configuration, or AliceMailService source changes are required. Private appsettings and migration code remain ignored, and the sample settings remain tracked.

## Manual review

1. Use a test database and a confirmed account. Keep your private configuration; no new settings or SQL scripts are needed. For email delivery, use the existing `Email`/`RabbitMQ` setup from the [Step 5 checklist](step-5.md#mail-configuration). Use a mailbox you control or a local SMTP capture service. The RabbitMQ queue must match the AliceMailService consumer. AliceMailService's `Email:MockSend=true` consumes messages without SMTP delivery; its “mock sent” line is not the email body. For actual delivery, configure the test consumer appropriately and disable mock sending there. Request a fresh link after changing that setting.
2. From the repository root, build the SPA and run AIS3 (or run the backend in Rider after the build):

   ```sh
   npm run build --prefix AliceIdentityService.Admin
   dotnet run --project AliceIdentityService
   ```

   The existing combined `npm run dev --prefix AliceIdentityService.Admin` workflow also works. Run one backend workflow on port 7443. Use HTTPS and a trusted local development certificate.
3. Open `https://localhost:7443/Account/ForgotPassword` while signed out. Try an invalid email, then request a link for your confirmed test account. Check that the received message contains a reset URL and one-hour expiry notice. Unknown or unconfirmed addresses show the same normal result without a reset email. Repeated requests within one minute should not generate another message.
4. Open the link. Opening it must not change your password. Try a weak password and mismatched confirmation; expect validation with empty password fields. Submit a valid new password. Expect a success page and an explicit sign-in link. The old password fails and the new password works. Reopen the used reset link: expect the invalid/expired/used-link page. Automated checks cover expiry, tampering, concurrent use, and the hourly cap without waiting or flooding mailboxes.
5. Sign in to the same test account in two browser sessions. In one session, open **Change password** from the profile. A wrong current password must not change the account. Submit the correct current password and a valid new password: the current browser signs out, and the other session must sign in again on its next protected request. New credentials work. Five wrong current-password attempts lock a lockout-enabled account for five minutes; perform this only on a disposable account.
6. At `https://localhost:7443/Account`, edit all three names and save. Expect a saved message and persisted values after reload and a new sign-in. Blank/overlength names fail validation. Email is displayed read-only. A non-admin account can edit its own profile but still cannot enter `/admin`. The fixed eight-hour/fourteen-day session expiries are unchanged.
7. For OIDC recovery, register/run the existing review client as described in the [Step 4 review](step-4.md), with AIS3 running:

   ```sh
   dotnet run --project AliceIdentityService -- review-client
   dotnet run --project tools/OidcReviewClient
   ```

   From `https://localhost:9443/`, start sign-in while signed out of AIS3 and choose **Forgot password?**. Expect the compact layout with the registered client name. Open the reset link in the same browser within ten minutes, reset, then use **Continue to sign in** and the new password. The client should complete authorization. If the continuation expires first, reset still works; restart client sign-in. Test an already-issued refresh token after a password operation: renewal must fail. After a profile-only edit, a successful renewal should contain the new names while retaining the existing `name=email/username` contract.
8. For a safe mail-failure check, start only your test AIS3 instance with an unused RabbitMQ port, such as `RabbitMQ__Port=1`. Request a reset for a confirmed test account. Expect the same generic response and a sanitized mail failure in the backend log, with no password change. Restore the port/restart, wait for the cooldown if necessary, and request a fresh link. A stopped consumer alone does not cause broker publication to fail; mail can remain queued. Leave shared brokers running.
9. Stop servers you started, including the review client, test mail consumer, and SMTP capture server. Leave shared PostgreSQL/RabbitMQ services alone. Review the uncommitted changes before approving Step 7.

## Automated checks

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
git diff --check
```

Adjust database settings for your machine. The admin connection creates/drops disposable databases; the restore connection points to a separate AIS2 dump restore and is read-only during checks. The RabbitMQ test uses a random isolated queue on port 5672 and `/`, consumes only its own messages, then deletes the queue. Optional `AIS3_TEST_RABBITMQ_USERNAME`/`AIS3_TEST_RABBITMQ_PASSWORD` override guest credentials. No automated test sends external SMTP email. The corresponding integration tests skip if their enabling variables are absent.

For focused checks, use `dotnet test --filter 'FullyQualifiedName~RecoveryProfileTests|FullyQualifiedName~MailTests'` with the same PostgreSQL/RabbitMQ variables. New tests cover reset/change, validation and CSRF, token lifetime/purpose/replay/concurrency, current-password lockout, generic mail-failure responses, shared limits, direct/OIDC continuation, cookie/code/refresh invalidation, profile authorization/overposting/escaping/persistence, fixed session expiry, refreshed claims, and reset template/MIME publication.

## Verification — 2026-11-15

- All 78 backend tests passed with PostgreSQL, a separate read-only AIS2 restore, and isolated RabbitMQ queues enabled; none were skipped. This includes the 19 focused recovery/profile/mail cases. The obsolete recovery-preview assertions now check a functional antiforgery-protected form with preserved OIDC context. Confirmation-mail expectations match the subject committed in Step 5.
- All 17 frontend tests passed, along with formatting, lint, TypeScript, and the React build. The backend built without warnings or errors, C# formatting verification passed, and publish output contains all six email-template files while excluding appsettings files.
- Browser review on an isolated HTTPS instance requested a reset, consumed its actual MIME message from a dedicated RabbitMQ queue, and opened the contained reset link. Profile editing persisted and showed the saved state; reset and change-password forms preserved the approved layout. Password submissions and OIDC recovery were exercised by the HTTP integration tests. No external email was sent and no existing user account was changed.
- Both SQL scripts and schema mappings are unchanged. Verification servers were stopped; temporary databases, queues, and captured test data were removed before handoff. Step 6 remains uncommitted and Step 7 has not started.
