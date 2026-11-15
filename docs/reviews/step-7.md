# Step 7 review

Steps 1–6 are approved and committed. Step 6 is commit `c2b9c03`. Step 7 is **uncommitted** for manual review. Step 8 has not started.

## Implemented behavior

- The approved React/Base UI shell loads the actual administrator's screen name and email. Account and sign-out links lead to the existing Razor pages; sign-out still requires confirmation and an antiforgery-protected POST.
- `GET /api/admin/session` and `GET /api/admin/overview` require the existing `ais_admin` policy. Anonymous/invalid sessions receive 401; authenticated non-administrators receive 403. The backend still protects the entire `/admin` namespace, including static assets and deep links. Vite continues to validate access through the same backend policy before serving development content.
- The SPA waits for a validated session before rendering protected routes. It rechecks on navigation, focus, page restore, visibility changes, and every minute while visible. A timer uses the remaining lifetime returned by the server, avoiding dependence on the workstation's wall clock. These requests do not renew the cookie. The eight-hour session and fourteen-day Remember-me policy is unchanged.
- Session expiry/sign-out/security-stamp rejection clears the administrator identity and page data and offers sign-in with a local return URL. Removed administrator access clears the shell and displays an access-denied state. Failed network/server requests clear protected content and provide retry. Requests have a ten-second timeout. An older pending session response cannot undo a later rejected data request. Switching to another signed-in user reloads the page data.
- The session response includes only user ID, email, first/last/screen names, original expiry, server time, and application metadata. No arbitrary claims, password hash, or stamp is serialized. API responses use `Cache-Control: no-store`; the SPA uses same-origin cookies and keeps data only in React memory, without localStorage/sessionStorage.
- The overview reads real counts and bounded registration/application summaries in one repeatable-read transaction. **Refresh** fetches a new snapshot. Session checks do not refresh the overview or reset its mounted page. Loading and empty states never substitute sample counts.

| Overview item | Meaning |
| --- | --- |
| Total users | All rows in `AspNetUsers`. |
| Connected applications | All registered OpenIddict clients, including clients without a display name. This does not measure online activity. |
| Unconfirmed accounts | Users with `EmailConfirmed = false`, including users who may also be locked. |
| Administrators | Distinct user accounts with at least one `ais_admin` claim. Claim values do not affect authorization; duplicates count once. This includes assigned accounts that are currently locked or unconfirmed. |
| Locked accounts | `LockoutEnabled = true` and `LockoutEnd` later than the server's current time. Expired/disabled lockouts do not count. |
| Recent registrations | At most five accounts, ordered by `CreationTime` descending, then ID ascending. Status shows Locked first when applicable, otherwise Confirmed/Unconfirmed. Only ID, name, email, status and creation time are returned. |
| Application cards | At most six clients, ordered by display name with client ID/record ID fallbacks and an ID tie-breaker. Only record ID, display name and client ID are returned. Secrets, properties, redirect URLs and permissions are excluded. |

Recent registrations have no links into the sample user detail pages. `/admin/users` and its detail routes remain the approved, clearly labeled sample UI until Step 8. Client/scope management remains in its own steps. The component page remains a local UI demonstration. No search/detail API, administrative mutation, or later-step operation has been added.

The architecture remains one ASP.NET Core backend, server-rendered account pages, and the React/Base UI admin SPA. There are no dependency, schema, migration, SQL, private-configuration, or mail-template changes. The mail transport tests now compare subjects with the committed template files, so editorial subject edits no longer require corresponding C# test edits.

## Manual review

1. Use a test database initialized or restored according to the [database guide](../database.md). No upgrade SQL or new configuration is needed. Use a confirmed administrator with an `ais_admin` claim and a separate confirmed ordinary account. Keep private appsettings ignored.
2. Build the SPA, then run the backend or launch it in Rider:

   ```sh
   npm run build --prefix AliceIdentityService.Admin
   dotnet run --project AliceIdentityService
   ```

   The existing combined `npm run dev --prefix AliceIdentityService.Admin` workflow remains available. Run only one backend workflow on port 7443. Use HTTPS.
3. While signed out, open `https://localhost:7443/admin/`. Expect the Razor sign-in page. Sign in as the administrator: the browser returns to the SPA, showing the actual screen name/email in the sidebar and actual database counts. No Alex Morgan/Campus Portal fixtures should appear on the overview. Your account opens the live profile page; changing the screen name there and returning to the admin tab should update the sidebar after access is rechecked.
4. Compare overview counts with the configured test database. These read-only queries are useful:

   ```sql
   SELECT COUNT(*) AS users,
          COUNT(*) FILTER (WHERE NOT u."EmailConfirmed") AS unconfirmed,
          COUNT(*) FILTER (WHERE u."LockoutEnabled" AND u."LockoutEnd" > CURRENT_TIMESTAMP) AS locked,
          COUNT(*) FILTER (WHERE EXISTS (
              SELECT 1 FROM "AspNetUserClaims" c
              WHERE c."UserId" = u."Id" AND c."ClaimType" = 'ais_admin'
          )) AS administrators
   FROM "AspNetUsers" u;
   SELECT COUNT(*) AS applications FROM "OpenIddictApplications";
   ```

   Check recent ordering, confirmation/lockout badges, and application names/client IDs. Changes made to the test data should appear after **Refresh**. Empty lists show explicit empty states. Recent rows are read-only; they do not navigate to fictional records.
5. Open **Users** and a sample user detail. Expect the existing preview layout and prominent sample labeling, with the real administrator still in the sidebar. Search/filter interactions remain local sample behavior; they do not query the database. Client/scope pages remain planned screens. This is the Step 8 boundary.
6. In the same browser, open a second admin tab. In one tab, choose **Sign out**, confirm on the Razor page, then return to the other tab. Expect the shell/data to disappear on focus or the next request and a **Sign in** link preserving the current admin path/query. Sign in again and verify normal access. The automated checks also advance the clock through both exact cookie lifetimes and verify no polling renewal.
7. In a separate private browser session, sign in as the ordinary account and visit `/admin/`: expect access denied. Direct navigation to `/api/admin/session` or `/api/admin/overview` must return 403 for that account, and 401 when signed out. The developer-tools Network panel should show JSON for authorized requests and `no-store` caching. Responses must not contain hashes, stamps, client secrets or arbitrary claim lists.
8. For an already-open administrator tab, automated checks cover a removed admin claim and password/security-stamp changes without modifying your accounts. A password change from Step 6 in another tab should produce the same re-sign-in state on return. If manually changing claims in a disposable test database, record and restore the original claims; removal must produce the SPA's administrator-access-required state on the next navigation, focus or polling check.
9. For a safe outage check, leave the SPA open and stop only your test AIS3 server. Click **Refresh** without reloading the whole browser document. Expect an unavailable/retry page with the prior data cleared. Restart the test server and choose **Try again**; valid access and current data should reload. Do not stop shared PostgreSQL or RabbitMQ services for this check.
10. Stop servers you started. Review the uncommitted changes before approving Step 8.

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

Adjust database settings for your machine. The admin connection creates/drops disposable databases; the restore connection must point to a separate AIS2 dump restore and is read-only during verification. RabbitMQ tests create/consume/delete only their own random queues and send no external SMTP email. Integration checks skip if their respective enabling environment variables are absent.

Focused checks: `dotnet test --filter FullyQualifiedName~AdminTests` with the database variables above, and `npm test --prefix AliceIdentityService.Admin -- src/pages/admin.test.tsx scripts/admin-access.test.ts`.

The new backend checks cover authorization, live claim changes, safe response fields, both fixed cookie lifetimes, profile freshness, stamp invalidation/logout, count definitions, deterministic bounded summaries, empty data, and a read-only AIS2 restore. Frontend checks cover startup gating, real/empty data, refresh, labeled preview routes, 401/403 clearing, focus/page-restore/visibility events, expiry with clock skew, periodic revalidation, outages/retry, stale-response races, and switching administrators.

## Verification — 2026-11-15

- All 84 backend tests passed with fresh PostgreSQL databases, a separate AIS2 restore, and isolated RabbitMQ queues enabled; none were skipped. All 34 frontend tests passed, including the existing development access checks, along with formatting, lint, TypeScript and Vite build checks. The backend built without warnings/errors and C# formatting verification passed. All six focused admin tests also passed after the final empty-display-name ordering check.
- Browser review used the built SPA with one isolated ASP.NET Core server, a disposable database, four fictional accounts and initially two clients. The overview showed 4 users, 2 clients, 1 unconfirmed account, 1 administrator and 1 locked account. Adding a test client and clicking Refresh changed the client count to 3 and showed its card.
- Browser checks confirmed the real sidebar identity, retained sample labels on user browsing, immediate removal of SPA content after an admin claim was revoked and a route was changed, re-sign-in after security-stamp invalidation, preserved return navigation, and denial of an ordinary account. Existing user accounts/databases were untouched and no email was sent.
- Verification servers were stopped and the disposable databases/work copy removed before handoff. Both SQL files and all private settings remain unchanged. Step 7 is uncommitted; Step 8 has not started.
