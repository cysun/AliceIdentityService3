# Step 10 review — Claims and scopes

Steps 1–10 are reviewed and committed; Step 9 is `eb775d0` and Step 10 is `5b469b0`. The checklist below records the Step 10 review. Current work is documented in the [Step 11 review](step-11.md).

## Delivered behavior

The approved React/Base UI shell now connects the user Claims tab and Scopes directory/details to the single ASP.NET Core backend. Razor account layouts and the AIS2 database contract are preserved. The shared Step 9 transaction lock, fresh actor check, antiforgery protection, optimistic versions and session/error boundary cover every new write.

| Operation | Behavior |
| --- | --- |
| Add user claim | Trim type/value; accept empty values; reject exact duplicate pairs, invalid types and identity/protocol-managed types. Different values for one type are supported. |
| Remove user claim | Confirm the target and type/value, then remove exactly the selected stored row. Imported duplicate/null rows can be removed individually. The current administrator cannot remove their own last `ais_admin` row. |
| Read scopes | Search by name/display name, with URL-backed query/page and server paging. Default 25 rows, ordered by name then ID; API page size 1–100. Missing details show a not-found state. |
| Create custom scope | Case-sensitive unique name, optional display name/description, claim types and resource identifiers. A scope does not assign user claims or grant a client permission. |
| Edit custom scope | Update display name, description, claim types and resources. Names are immutable identifiers. Unrelated imported properties and localized labels/descriptions are retained. |
| Delete custom scope | Requires typing the exact name (ID if an imported row has no name). Refused while any registered client permits the scope or any valid authorization includes it. |
| Standard scopes | Existing standard records are shown read-only, with their effective AIS2 claim mappings. `openid`, `email`, `profile`, `address`, `phone` and `offline_access` names cannot be created/edited/deleted through administration. Bootstrap remains responsible for missing standard records. |

Any `ais_admin` value, including empty or `false`, grants full administration access, as in AIS2. Claim writes rotate security/concurrency stamps and revoke the user's persisted OIDC tokens/authorizations in the same transaction. A failure rolls back all changes. The user must sign in again; own-claim changes end the current administrator session immediately. Older confirmation/recovery links also become invalid. Existing independently validated JWTs and client-local sessions may remain valid until their own expiry.

Scope edits intentionally retain current cookies/grants. Subsequent authorization-code exchanges, refreshes and userinfo responses rebuild claims using the current definition. A claim reaches ID/access tokens only when it exists on the user and its scope is granted to an allowed client; the existing standard-scope destination rules are unchanged. Custom scope resources become access-token audiences. Removing a scope mapping stops future release but cannot alter already-issued JWTs. Private security/protocol claims never receive public destinations, even if an imported definition lists them. See [OpenIddict's claim-destination documentation](https://documentation.openiddict.com/configuration/claim-destinations) for the underlying distinction.

Client settings/permissions remain Step 11. No automatic client-permission changes, consent screen, notification email, bulk claim operations, schema changes, new dependencies or configuration are included. Private appsettings.json and migration code remain ignored; appsettings.sample.json stays tracked. Both versioned SQL scripts remain unchanged.

## Manual review

Use a test database with the approved Step 8 search setup, one confirmed administrator, and disposable ordinary accounts. Keep another administrator available when testing own-claim changes. Mail services are unnecessary for these operations.

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

Open https://localhost:7443/admin/. Alternatively use `npm run dev --prefix AliceIdentityService.Admin` after stopping any process using the same port. Keep private configuration pointed at your test database; never run CreateSchema.sql over an existing database.

1. Open an ordinary user's **Claims** tab. **Add claim** identifies the user and explains session/grant invalidation. Cancel first: nothing changes. Add `department = Computing`, then `department = Mathematics`. Both rows should persist. Repeating the exact pair must report validation without closing the dialog. Markup-looking values must display as text. Empty values are allowed. Try blank/whitespace types, `sub`, `email`, `oi_scopes`, and `AspNet.Identity.SecurityStamp`: reject them.
2. Sign the ordinary user in with another browser before another claim change. Its AIS session and existing authorization codes/refresh tokens must stop working after the change. Sign in again and check the stored claims. Own-claim changes must clear the SPA and show **Please sign in again**. On your own account, the last admin claim's Remove button is disabled and the API rejects removal too. To replace a claim, remove the old row and add the new pair; each write is a separate confirmed change.
3. Remove one department row, first cancelling and then confirming. Only that row disappears. On an AIS2 test restore, imported duplicate/null rows can be removed individually. Every removal identifies the target/type/value. Add/remove on one account must never affect another account's claim ID.
4. Open the same account in two tabs, then add a claim in one. Submitting from the second must report a stale version and require **Reload details**. Check that simultaneous administrators cannot remove each other's admin access using already-admitted requests.
5. Open **Scopes**. Inspect standard definitions and their read-only state. Search by name or display name; Refresh reloads data. Check empty results, pagination and browser Back/Forward. Standard mappings should match the existing AIS2 protocol, even if an imported standard row has a different `claims` property.
6. **Create scope** named `review_courses`, with display name `Review Courses`, description, claim types `department` and `role` on separate lines, and resource `review-courses-api`. Cancel once, then submit. It opens real details. Try duplicate names, spaces in names, duplicate claim/resource lines, and private protocol claim mappings: validation stays in the dialog. Names are case-sensitive; no wildcard behavior is implied by search input.
7. **Edit scope**: the name is read-only. Change the description, claims and resources, then **Save changes**. Reload to verify persistence. Open two edit dialogs, save one, then submit the other: require reload on conflict. Standard definitions have no edit/delete actions; direct writes are also rejected.
8. Open **Delete scope**. Submission stays disabled until the exact name is entered. Cancel, then delete an unused disposable definition and confirm it disappears. A scope permitted by an existing test client must be refused. A valid authorization also blocks deletion even if client permissions have been removed. Revoke grants through their owning account workflow and remove client permission before deletion; client-permission UI is deferred to Step 11. Do not directly modify production SQL to bypass this check.
9. For protocol review, use an existing test client already permitted to request a custom scope (or the automated test fixture below). Use the [Step 4 client instructions](step-4.md) for the sign-in flow; its default standard permissions alone cannot test custom claims. With `department` granted, both assigned department values should appear in fresh ID/access tokens and userinfo; unrequested/unpermitted scopes must not release them. Change the custom definition's claims/resources: refresh should use the new mapping/audience, and userinfo should use the new mapping. Existing JWT content stays unchanged. Add/remove an assigned user claim: old codes/refresh tokens fail; a fresh sign-in gets current values. Never paste tokens into a third-party decoder.
10. Inspect Network: every write uses the current opaque `version`, same-origin cookie and fresh `X-CSRF-TOKEN`; scope creation has no version. No mutation occurs on GET. Anonymous/non-admin requests return 401/403, missing/forged CSRF returns 400, stale edits 409, missing user/scope 404. No raw security/concurrency stamps or arbitrary scope properties appear in JSON. Sign out or switch administrators in another tab before submitting: the protected UI clears without applying the form as the other identity.
11. Stop the test web server while a form is open, then submit. The existing unavailable boundary requires reloading current state; it never replays the write. Repeat checks at a narrow viewport and using keyboard navigation, including Cancel/Escape, focus return, scrolling long dialogs and pending submission controls.
12. Stop all verification servers. Review the uncommitted changes and authorize Step 11 separately.

## API and limits

All endpoints require the current administrator cookie and return `Cache-Control: no-store`. Unsafe requests require JSON and `X-CSRF-TOKEN` from `/api/admin/antiforgery`, with the existing user-bound token check. Request bodies are limited to 48 KiB.

| Method/path | Fields |
| --- | --- |
| POST `/api/admin/users/{id}/claims` | `version`, `type`, required `value` (empty allowed) |
| DELETE `/api/admin/users/{id}/claims/{claimId}` | `version`; ID must belong to this user |
| GET `/api/admin/scopes` | Query: `q`, `page`, `pageSize` |
| GET `/api/admin/scopes/{id}` | Safe definition fields, `standard`, derived `version` |
| POST `/api/admin/scopes` | `name`, optional `displayName`/`description`, required `claims`/`resources` arrays |
| POST `/api/admin/scopes/{id}` | Same fields plus `version`; name must match |
| DELETE `/api/admin/scopes/{id}` | `version`, `confirmation` |

User detail now includes claim row `id` alongside `type`/`value`. Claim types are at most 256 characters without whitespace/control characters; values are at most 4096 characters without control characters. Scope names are at most 200 ASCII scope-token characters, excluding spaces, quotes and backslashes. Display name is at most 200 characters, description 2000. Scope arrays each allow at most 100 unique entries. Resource identifiers are at most 2000 characters without whitespace/control characters. API search is at most 200 characters; pages are 1–10,000 and clamp to the last available page. UI textareas trim lines and omit blank lines; the API rejects invalid/duplicate entries rather than silently merging them.

Mutations return `{ id, sessionEnded }`. Logs record actor ID, operation and target ID without claim values. Scope writes share the administrative lock and revalidate the actor after acquiring it; direct SQL/AIS2 writers do not participate in this boundary. Concurrent mixed-version administration is outside this development workflow.

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

Adjust connections for your machine. The administrator connection creates/drops disposable databases; the restore connection must point to a separate AIS2 restore with PopulateSchema.sql already applied, following the [database guide](../database.md). RabbitMQ checks use random queues without external SMTP delivery. Tests skip when their enabling variables are absent. Build the frontend before backend HTTP tests.

Focused commands, with the PostgreSQL variable above set:

```sh
dotnet test --filter FullyQualifiedName~AdminClaimScopeTests
npm test --prefix AliceIdentityService.Admin -- src/pages/claims-scopes.test.tsx
```

Verified for this handoff: **110 backend tests passed, none skipped; 61 frontend tests passed**. The full backend run included the read-only AIS2 restore and RabbitMQ checks. The added cross-instance scope-freshness assertion also passed in a focused follow-up. Frontend formatting/lint/TypeScript/build, backend format verification, solution build and diff checks passed.

Coverage includes every new write's authorization/CSRF boundary, claim validation/duplicates/legacy row removal, own-admin protection, stale and simultaneous writes, real cookie/code/refresh-token invalidation, scope validation/metadata preservation/standard protections/deletion guards, public token destinations and resources, and cross-instance definition freshness. A disposable PostgreSQL trigger proves claim-row, stamp and grant rollback on revocation failure.

Browser review used fictional data in a fresh temporary database: standard/custom scope presentation, successful scope creation/editing, deletion's disabled confirmation state and cancellation, claim assignment, own-session invalidation and persistence after a fresh sign-in. Deletion execution and concurrent cases were covered by automated HTTP/UI tests. Narrow viewport and broader keyboard checks remain in the manual checklist. Local screenshots are saved under ignored `artifacts/reviews/step-10-scopes.jpg` and `step-10-claims.jpg`.

The verification server was stopped and port 7443 was confirmed free. Both temporary review/restore databases, the AIS2 dump and test logs were removed. No AIS2 or shared AIS3 database was modified. Step 11 had not started at that handoff; see the current Step 11 review linked above.
