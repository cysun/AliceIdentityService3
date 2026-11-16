# Step 11 review — Clients

Steps 1–11 are reviewed and committed; Step 11 is `07e10f0`. This checklist records the Step 11 handoff. Current work is documented in the [Step 12 review](step-12.md).

## Delivered behavior

The approved React/Base UI shell now connects the Clients directory, client details and overview application links to the single ASP.NET Core backend. Razor account pages and AIS2 database mappings are preserved. No SQL, schema, application configuration or dependency changes are needed. Private appsettings.json and migration code remain ignored; appsettings.sample.json stays tracked. CreateSchema.sql and PopulateSchema.sql are unchanged.

| Operation | Behavior |
| --- | --- |
| Directory/details | Case-insensitive substring search over client ID/display name; URL-backed search/page; default 25 rows, sorted by display name (client ID fallback), then internal ID. Safe detail fields include secret presence, never the secret/hash or arbitrary imported properties. |
| Register | Public or confidential web application, authorization-code flow, implicit consent. Stable, case-sensitive client ID; display name; redirect and post-logout URLs; allowed scopes; refresh-token permission; PKCE. |
| Edit | Change display name, callbacks, scopes, refresh permission and PKCE. IDs cannot change. Ordinary settings edits preserve the secret hash and unrelated imported metadata. |
| Credentials | Separate dialog and endpoint to rotate a secret or change client type. Requires typing the client ID. Confidential requires a new secret; public removes the secret and enables PKCE. |
| Delete | Requires the exact client ID (internal record ID if an imported client has no public ID). Removes the application and its persisted authorizations/tokens. Reusing its public ID cannot revive old grants. |
| Imported clients | Retain existing callbacks unchanged, including custom native schemes and legacy HTTP URLs, plus application/consent type, other permissions/requirements, settings, properties and localized names. Profiles outside public/confidential + implicit consent + authorization-code/code-response permissions have read-only settings/credentials; confirmed deletion remains available. |

Public clients must require PKCE when registered or saved. An imported public client without PKCE can still be inspected and used unchanged, but saving its settings requires enabling PKCE; the UI explains this. Confidential clients default to PKCE and can make it optional. The server's existing S256-only behavior is unchanged. New URLs must be absolute HTTPS, or HTTP with host `localhost` or a loopback IP, without credentials, fragments, wildcards or control characters. Unchanged imported URLs are retained; this step does not silently rewrite them.

Allowed scopes must be standard or registered custom names; existing unknown imported permissions can be retained unchanged. Adding a scope permission does not assign user claims or create a scope. `openid` is always available; `offline_access` depends on refresh-token permission, even when absent from the explicit scope list. See OpenIddict's [application permissions](https://documentation.openiddict.com/configuration/application-permissions.html) and [PKCE requirements](https://documentation.openiddict.com/configuration/proof-key-for-code-exchange.html).

The secret form starts empty, masks by default, supports Show/Hide, and can generate 32 random bytes locally using the browser's cryptographic random generator (43 base64url characters). Save the new secret in the application's private configuration before submitting. It cannot be retrieved afterward. Closing/reopening or changing type clears the form's secret. No local/session storage, query string, API response or success log contains it. OpenIddict hashes a newly supplied secret; it retains the existing hash for ordinary settings edits. Credentials are never accepted by the ordinary settings endpoint.

Changes to redirects, post-logout URLs, allowed scopes, refresh-token permission, PKCE or credentials revoke **that client's** persisted tokens and authorizations in the same transaction. Existing authorization codes and refresh tokens stop working. Display-name-only edits retain grants. These operations leave AIS user cookies and other clients' grants intact. Already-issued self-contained JWTs and application-local sessions may remain valid until their own expiry. Deletion removes associated protocol rows; any failure rolls back the whole operation.

All writes reuse the shared administrative transaction lock, fresh actor check, antiforgery protection and optimistic edit version. The browser rechecks the acting identity before writing, prevents duplicate submission, requires reload on stale/uncertain outcomes and never automatically replays a mutation. Logs contain actor ID, operation and internal client ID, not secrets. Direct SQL or concurrent AIS2 writers do not participate in this lock.

## Manual review

Use a disposable test database prepared according to the [database guide](../database.md), with a confirmed administrator and ordinary test account. Keep any existing private configuration pointed at that test database. Never run CreateSchema.sql over an AIS2 restore. Mail delivery is not needed for client management.

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

Open https://localhost:7443/admin/clients. The existing development runner is also supported; do not run both on port 7443.

1. Inspect the directory and open existing details. Search by client ID/display name, try literal `%`/`_`, an empty result, paging, Refresh and browser Back/Forward. Overview application links should open client details. Markup-looking names and URLs must display as text. Details show only whether a secret is configured.
2. Open **Register client**, then Cancel. Reopen and register a public client `step11-public` named `Step 11 Public`, with redirect `https://localhost:9443/signin-oidc`, post-logout URL `https://localhost:9443/signout-callback-oidc`, and `profile`/`email` scopes on separate lines. Leave refresh tokens and PKCE enabled. It should open persisted details. No secret field is shown for public clients.
3. Try duplicate IDs, a blank ID/name, whitespace in an ID, missing redirects, duplicate URL/scope lines, unregistered scopes, a relative URL, a remote HTTP URL, user-info credentials, a wildcard or a fragment. Validation should stay in the dialog. Public clients must reject PKCE disabled. HTTPS and HTTP localhost/IPv4/IPv6 loopback callbacks are accepted. Client IDs and scope permissions are case-sensitive.
4. Register `step11-confidential` with the same callbacks/scopes and **Confidential — server application**. Generate a secret, inspect Show/Hide, and store it privately before submitting. A secret shorter than 32 characters is rejected. Cancel/reopen once to confirm the secret clears. After successful creation, reads and settings forms must never expose the old secret or hash.
5. **Edit client** has a read-only ID and no credential field. Change only the display name and verify persistence; existing refresh tokens must still work. Change a callback, allowed scope, refresh permission or PKCE: old codes/refresh tokens must fail, and fresh sign-in must use the new configuration. Other clients and the current AIS administrator session remain usable. Imported metadata and unchanged callbacks should survive.
6. Open the same client in two tabs, save in one, then submit the other: expect a stale-version message requiring **Reload details**. Sign out or change administrators in another tab before submitting: the protected UI must clear without applying the form as the other user. Pending submissions disable the form/close controls. A network or server failure requires reloading current state, without replaying the write.
7. **Change credentials** identifies the client, explains revocation and requires its exact ID. Rotate a confidential secret and update the test application's private configuration; the old secret and old grants must fail. Convert to public: the hash disappears and PKCE is enabled; fresh code + PKCE works without a secret. Convert to confidential with a new secret: fresh requests must authenticate the client. Cancel must make no change.
8. On an AIS2 test restore, inspect a representative imported client. Verify existing callbacks, scopes, application/consent type and unrelated settings remain intact after a supported edit. An imported public client without PKCE must explain the required change before saving. Unsupported consent/type/flow configurations show a read-only explanation and reject direct settings/credential writes too.
9. Create an unused custom scope, permit it on a test client, and try deleting it: deletion must be refused. Remove that client permission (which revokes its grants), then delete the scope if no other client or valid authorization references it. The scope's user claims are separate and remain assigned.
10. Open **Delete client**, cancel once, then enter the exact ID and confirm deletion of a disposable client. It must leave the directory; old code/refresh grants fail. Registering a new client with the same public ID must not restore old grants. Already-issued JWTs and local application sessions retain their own lifetime.
11. Inspect Network: writes carry the current `version` and fresh `X-CSRF-TOKEN`, except creation has no version. Anonymous/non-admin reads and writes return 401/403; missing/forged CSRF is 400; stale versions 409; missing records 404. GET never mutates data. No response contains a secret hash, raw concurrency token or unrelated imported properties.
12. Review keyboard navigation, Cancel/Escape/focus return, long-dialog scrolling and narrow-screen layout. Stop all verification servers, review the diff and authorize Step 12 separately.

### Public/confidential OIDC exercise

The standalone review client retains its Step 4 defaults and now accepts `ClientId`, optional `ClientSecret` and space-separated `RequestedScopes` through normal .NET configuration. It always uses authorization code + S256 PKCE, with the callback URLs above. Start one review client at a time and use a fresh browser session when switching registrations.

For the public client created in the checklist:

```sh
ClientId=step11-public dotnet run --project tools/OidcReviewClient
```

Open https://localhost:9443, sign in through AIS3 as the ordinary account, **Renew tokens**, and **Sign out through AIS3**. The review client verifies refreshed ID-token signature/issuer/audience/subject. Its claims table uses userinfo. Test confidential mode by setting `ClientId=step11-confidential` and supplying the new `ClientSecret` privately through your IDE's local run configuration or an interactive hidden environment-variable prompt, then starting the same project. Do not put secrets in tracked files, command-line arguments, shell history or screenshots. Both initial exchange and renewal send the configured secret.

For a custom-scope flow, create a scope such as `review_courses` mapped to `department`, assign a `department` claim to the ordinary user, and permit `review_courses` on the client. Start the review client with `RequestedScopes='openid profile email offline_access review_courses'`. A fresh sign-in must release the assigned claim; omitting or disallowing the custom scope must prevent release. Scope mapping edits apply on new issuance/refresh/userinfo; assigned-claim edits invalidate the user's old grants. Removing a client's permission invalidates that client's old grants. If refresh is disabled, omit `offline_access` from the requested scope string. Keep tokens local.

To exercise old-grant rejection, leave a review-client session open while editing that registration in the admin browser, then use **Renew tokens**. After a security-related change it should report rejection. Stop the reviewer on port 9443 after testing. For wrong-secret, missing/wrong PKCE, invalid redirect and cross-client replay cases, the HTTP integration tests below provide reproducible checks.

## API and limits

All endpoints require a current administrator cookie, return `Cache-Control: no-store`, and use internal record IDs in paths. Unsafe requests require JSON and a user-bound `X-CSRF-TOKEN` obtained from `/api/admin/antiforgery`. Bodies are limited to 48 KiB.

| Method/path | Fields |
| --- | --- |
| GET `/api/admin/clients` | `q`, `page`, `pageSize` |
| GET `/api/admin/clients/{id}` | Safe fields, `canManage` and derived `version` |
| POST `/api/admin/clients` | `clientId`, `displayName`, `clientType`, optional `clientSecret`, required `redirectUris`/`postLogoutRedirectUris`/`scopes` arrays and `allowRefreshTokens`/`requirePkce` booleans |
| POST `/api/admin/clients/{id}` | Same settings and `version`; excludes `clientType`/`clientSecret`; ID must match |
| POST `/api/admin/clients/{id}/credentials` | `version`, `clientType`, optional `clientSecret`, exact-ID `confirmation` |
| DELETE `/api/admin/clients/{id}` | `version`, exact-ID `confirmation` |

IDs are at most 100 characters with no whitespace/control characters after trimming; display names are required and at most 200 characters. Redirect lists allow up to 100 entries each, each at most 2048 characters; at least one sign-in redirect is required. Scopes allow up to 100 unique names, each at most 200 ASCII scope-token characters. Secrets are 32–256 printable ASCII characters without spaces. API search is at most 200 characters; page is 1–10,000 and clamps to the last available page; page size is 1–100. UI textareas trim lines/omit blank lines, with a combined request-size bound. The server rejects duplicate entries rather than silently merging them. Mutations return `{ id, sessionEnded: false }`.

## Automated checks and handoff

Build the frontend before backend HTTP tests. PostgreSQL variables enable disposable database tests and the separate read-only AIS2 restore checks. Apply PopulateSchema.sql to the separate restore before testing; do not use the source AIS2 database as a test target. RabbitMQ tests use random local queues without SMTP delivery.

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
dotnet build tools/OidcReviewClient
git diff --check
```

Adjust connection values for your machine. Tests skip when their enabling variables are absent. Focused commands, with the administrator connection variable set:

```sh
dotnet test --filter FullyQualifiedName~AdminClientTests
npm test --prefix AliceIdentityService.Admin -- src/pages/clients.test.tsx
```

Verified for this handoff: **121 backend tests passed, none skipped; 70 frontend tests passed**. Backend coverage includes the read-only AIS2 restore, RabbitMQ checks, public/confidential code + PKCE + refresh + userinfo, validation/access/CSRF, secret non-disclosure, imported metadata/native callback preservation, unchanged-secret authentication, all five security-setting revocation categories, credential rotation/type conversion, deletion/ID reuse, rollback on injected failures, stale/concurrent writes, fresh actor validation, cross-instance credential/CORS freshness and scope-deletion dependencies. Frontend tests cover directory/search, create/edit payloads, secret generation/masking/clearing, confirmation/pending controls, stale versions, identity-switch cancellation and unsupported imports. Formatting/lint/TypeScript/build passed; backend and standalone reviewer builds passed with no warnings.

Browser review used a fresh disposable database: directory/details, public/confidential registration fields, dialog cancellation/focus return and a persisted display-name edit were checked. Secret/type mutations and deletion were exercised through HTTP integration tests; the full manual checklist remains the review gate. A screenshot is in ignored `artifacts/reviews/step-11-client-detail.jpg`.

The temporary verification server is stopped. Disposable Step 11 databases and the temporary AIS2 dump are removed after checks; the source AIS2 database and private application configuration are untouched. At that handoff, changes were uncommitted and Step 12 had not started; see the current review linked above.
