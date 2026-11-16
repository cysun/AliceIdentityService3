# Step 9 review — Administrative account operations

Steps 1–8 are approved and committed (Step 8: `e5ef6eb`). Step 9 is implemented and **uncommitted for manual review**. Stop before Step 10.

## Delivered behavior

The existing React/Base UI directory and Profile/Security tabs now support the AIS2 administrative account operations and lockout controls through the single ASP.NET Core backend. The Razor account pages, approved layout and Step 8 search contract are preserved.

| Operation | Behavior |
| --- | --- |
| Create account | Required first/last/screen names, email and matching password confirmation. Creates a confirmed ordinary account with email as username, after the administrator verifies the address. No administrator claim or email message is generated. Case-insensitive existing email/username collisions are rejected. |
| Edit account | Changes only first/last/screen names and email confirmation. Names are trimmed, required and limited to 255 characters. Email and username remain read-only. Extra security/claim fields cannot be assigned through this endpoint. |
| Reset password | Sets a new password using the existing Identity policy: at least six characters, upper/lowercase, digit and symbol. Password fields are limited to 4096 characters and must match. No email is sent. Lockout and two-factor settings are preserved. |
| Lock account | Enables lockout and sets its end to one hour, 24 hours or until explicitly unlocked. It blocks password and OIDC sign-in. |
| Unlock account | Clears lockout end and failed attempts. Preserves the lockout-enabled flag, confirmation and two-factor settings. Rotates the stamp even for an account that was not currently locked, so old sessions cannot revive. |
| Delete account | Permanently removes the user and related Identity rows. Requires typing the exact email address, or ID if email is absent/empty. OIDC records are revoked and retained for normal protocol cleanup. |

Names-only changes retain sessions and grants. Confirmation changes, reset, lock, unlock and deletion invalidate the account's AIS cookies and OIDC codes/refresh tokens and revoke persisted tokens/authorizations. Outstanding confirmation/recovery tokens become invalid when the stamp changes. These writes and revocations share a database transaction; a failure rolls back the operation. Independently validated JWTs already held by client applications remain subject to their existing expiry (one hour by default); this step does not remotely end an application's own session.

The current administrator cannot lock, delete or unconfirm their own account. Resetting their own password or unlocking their own account ends their AIS session. Administrative writes serialize across instances and recheck the actor's account/stamp/current `ais_admin` claim after acquiring the transaction lock. Simultaneous admitted administrators therefore cannot disable each other using stale access. This does not introduce a general recovery mechanism for an already unusable administrator account.

Every form identifies the target and explains its effect before submission. Password values clear when the dialog closes. Pending submissions cannot be repeated or dismissed; validation stays in the form. A stale version or missing account requires reload. Authentication failures clear protected content. For uncertain server/network outcomes, the UI asks the administrator to reload and check the result; it never automatically replays the write. Success logs contain actor ID, operation and target ID, not submitted credentials or reset tokens.

Claim assignment, scopes, client editing, email-address changes, two-factor configuration, notification emails for administrative actions and bulk operations are outside Step 9. Claim/scope management remains Step 10.

## Configuration and SQL

No new configuration, dependencies, schema changes or SQL are required for Step 9. Use the existing private appsettings.json and the approved Step 8 database/index. For a new database or separate AIS2 restore, follow the [database guide](../database.md), including PopulateSchema.sql. Do not rerun CreateSchema.sql over an existing database.

Both versioned SQL scripts, private settings, tracked appsettings.sample.json and ignored migration source are unchanged. No shared AIS2/AIS3 database was modified during implementation verification.

## Manual review

Use a test database, a confirmed administrator and disposable ordinary accounts. Keep another usable administrator available when checking own-password reset. RabbitMQ/AliceMailService is not needed for these administrative operations.

1. Build and run from the repository root:

   ```sh
   npm run build --prefix AliceIdentityService.Admin
   dotnet run --project AliceIdentityService
   ```

   Open https://localhost:7443/admin/users. Rider can run the same project after the React build. Alternatively use `npm run dev --prefix AliceIdentityService.Admin`, after stopping any server using the same port.
2. Select **Create account**. Cancel first and confirm no account is created. Try an invalid email, blank names, weak/mismatched passwords and an existing email with different letter case. Then create a disposable account with a valid password. It should open its real details, be confirmed, have no claims, and sign in as an ordinary user with no admin access. No email should arrive.
3. In **Profile → Edit account**, change the three names and save. Confirm trimmed values in details, directory search and the user's own profile. Email stays unchanged. Cancel another edit and verify nothing changes. Use markup-looking text in a test name: it must appear as text.
4. Open the same account in two tabs and open both edit dialogs. Save one, then save the other. The second must report that the account changed and require **Reload account** before another submission. This also applies to security actions; normal failed-login/password/profile updates can invalidate the displayed version.
5. Sign the ordinary account in using a separate browser/session. As administrator, uncheck **Email confirmed** and save. Its existing AIS session must end, and subsequent sign-in must be refused. Confirm it again and verify a fresh sign-in works. The administrator's own confirmation checkbox is disabled, with equivalent server protection.
6. In **Security → Reset password**, first cancel, then reopen: password fields should be empty. Try a weak password and mismatch, then set a valid replacement. The old password, previously issued reset link and old AIS session must fail; the replacement password must work. For OIDC, use the [Step 4 review client](step-4.md): old codes/refresh tokens must fail after a security operation. Existing application-local sessions or independently validated JWTs may remain until their own expiry.
7. **Lock account** explains the effect and offers one hour, 24 hours and until unlocked. Check the saved status/end, directory Locked filter, overview count and blocked sign-in. **Unlock account** must clear the end and failed count, preserve the Security tab, and require a fresh user sign-in. A reset while locked must not clear lockout or two-factor flags.
8. For a disposable account, open **Delete account**. The submit button is disabled until the exact email/ID is entered. Cancel and verify it remains. Reopen and delete: return to the preserved directory query; the account disappears, its old detail returns 404 and its old sign-in/grants no longer work. The current administrator's Lock/Delete buttons are disabled and direct requests are rejected.
9. Reset your own password only when ready to end the current session. The SPA must immediately show **Please sign in again**; sign in with the new password. Confirmation changes and password values must never appear in URLs or persistent browser storage.
10. Inspect developer-tools Network requests. Each write first obtains `/api/admin/antiforgery`, then uses the same-origin cookie, `X-CSRF-TOKEN`, JSON and the current opaque `version` (except creation). Tokens/JSON responses are not cached. Raw hashes/stamps/reset tokens are absent from user detail responses. Missing/forged CSRF tokens return 400; anonymous/non-admin callers receive 401/403. GET requests cannot perform mutations.
11. With a form open, sign out in another tab or switch to another administrator, then submit. The protected shell must clear without applying the form as the other identity. To exercise a connection failure, stop only the test web server before saving. Expect the unavailable boundary; restart, choose **Try again**, and check the account before submitting again. No write should replay automatically.
12. Stop your verification server. Review the changes and authorize Step 10 separately.

## API contract

All endpoints require the current administrator cookie; responses use `Cache-Control: no-store`. Unsafe requests use JSON and the `X-CSRF-TOKEN` header from the administrator-only token endpoint. The antiforgery cookie remains HttpOnly/Secure/SameSite=Strict. Existing Razor antiforgery forms continue to work.

| Method/path | JSON fields |
| --- | --- |
| GET `/api/admin/antiforgery` | Returns `userId`, `requestToken`; the SPA checks that the actor still matches the visible session. |
| POST `/api/admin/users` | `firstName`, `lastName`, `screenName`, `email`, `password`, `confirmPassword` |
| POST `/api/admin/users/{id}/profile` | `version`, `firstName`, `lastName`, `screenName`, required `emailConfirmed` |
| POST `/api/admin/users/{id}/password` | `version`, `password`, `confirmPassword` |
| POST `/api/admin/users/{id}/lock` | `version`, `duration` (`hour`, `day`, `indefinite`; default `day`) |
| POST `/api/admin/users/{id}/unlock` | `version` |
| DELETE `/api/admin/users/{id}` | `version`, `confirmation` |

Detail GET adds an opaque `version`, derived from ID and Identity concurrency stamp. Mutation success returns `{ id, sessionEnded }`; own security changes also clear the cookie. Invalid inputs/Identity policy failures return 400, a missing account 404, and stale/competing updates 409. Unauthorized/forbidden remain 401/403 without login HTML. Request bodies are limited to 48 KiB. Arbitrary entity binding and GET-based writes are not supported.

## Automated checks

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
git diff --check
```

Adjust the connections for your machine. The admin connection creates/drops isolated databases. `ais3_copy` must be a separate AIS2 restore with the Step 8 PopulateSchema.sql already applied, as described in the database guide. RabbitMQ tests use random queues; no external SMTP mail is sent. Integration tests skip when their enabling variables are absent. Build the frontend before backend tests.

Focused commands (set the PostgreSQL variable above):

```sh
dotnet test --filter FullyQualifiedName~AdminUserOperationTests
npm test --prefix AliceIdentityService.Admin -- src/pages/user-operations.test.tsx
```

Verified for this handoff: **101 backend tests passed, none skipped; 54 frontend tests passed**. Frontend formatting/lint/TypeScript/build, backend format verification and solution build passed. Coverage includes every write's authorization/CSRF requirements, validation, overposting, duplicate creation, persisted search updates, optimistic concurrency, self-protection, all lock durations, password/confirmation changes, real cookie/code/refresh/reset-link invalidation, deletion and concurrent administrators. A PostgreSQL trigger that deliberately fails authorization revocation proves rollback of password/lock/delete and preceding token writes. The trigger exists only in its disposable test database.

Browser verification used fictional accounts in a separate temporary database: real admin sign-in, a saved profile edit reflected in directory/details, Create/Lock/Delete dialog presentation, cancellation and deletion's disabled confirmation state. No browser console warnings/errors appeared. Credential and deletion submissions are covered by HTTP integration and UI tests; they remain in the manual checklist too. The verification server was stopped, its browser tab closed, and temporary databases/dump removed. Step 10 has not started.
