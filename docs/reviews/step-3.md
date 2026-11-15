# Step 3 review

Step 3 is approved and committed as `9c2c7fa`. The checklist and verification below record the Step 3 handoff; current OIDC behavior and review instructions are in the [Step 4 review](step-4.md).

## Implemented behavior

- Real server-rendered direct email/password login, the existing approved split layout, a minimal signed-in account landing page, an access-denied page, and POST sign-out. GET logout is confirmation only.
- Confirmed email required; five failed passwords cause a five-minute lockout for lockout-enabled accounts. A single generic message covers wrong credentials, missing accounts, unconfirmed email, and lockout. Imported two-factor-enabled accounts fail closed because AIS3 has no challenge flow yet.
- Antiforgery on unsafe MVC actions, password never rendered back into HTML, local return URLs, and no-store account/admin/API responses.
- Secure, HttpOnly, host-only `__Host-AIS3.Session` cookie with SameSite=Lax. Eight-hour fixed session ticket, or a fourteen-day fixed persistent ticket with Remember me. No sliding renewal. Nonpersistent browser cookies can survive browser session restoration; server expiry still applies.
- Every authenticated request validates the security stamp, account existence, confirmation and lockout state, and rebuilds current claims. Admin claim removal takes effect on the next request without waiting for cookie expiry. This intentionally incurs database reads per request.
- The actual AIS2 claim **`ais_admin`**, regardless of its stored value, controls admin access. Neither the hyphenated spelling nor a role grants access. Admin HTML/deep links/assets and `/api/system/info` are protected. APIs return 401/403; browser pages challenge or show access denied. The Vite preview asks the backend to authorize access and fails closed if it is unavailable.
- Sign-out clears cookies in the current browser. It is not global session revocation and does not revoke OIDC tokens. A copied cookie remains usable until expiry or an account/security-stamp change. Production Data Protection storage, key migration, and cutover session continuity remain deferred.

The React/Base UI shell and fictional data remain previews. Its account link now leads to the real account/sign-out page, but its displayed sample administrator is not the signed-in identity. OIDC, registration, confirmation, recovery, profile editing, and live admin APIs are not implemented. The host remains Development/Testing-only.

No database model or SQL changes were needed. `CreateSchema.sql` and `PopulateSchema.sql` are unchanged. Migration code and private configuration remain ignored; `appsettings.sample.json` stays tracked.

## Automated checks

From the repository root:

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun' \
dotnet test
dotnet format --verify-no-changes --no-restore
git diff --check
```

Adjust the database host/user. The admin connection needs permission to create/drop disposable databases; tests never seed or mutate the configured application database. The restore variable must point to a separate AIS2 dump restore, prepared using the [database guide](../database.md); its test uses a read-only transaction. Without these variables the respective integration tests are skipped.

Tests exercise real Identity password verification, PostgreSQL state changes, HTTP forms/antiforgery, encrypted cookies, fixed expiry with a controlled clock, admin HTML/assets/API access, claim removal, security-stamp changes, deleted/locked/unconfirmed accounts, redirects, and the unchanged preview routes. UI tests also cover the Vite authorization boundary and backend unavailability.

## Manual review

1. Use a test database initialized/restored as described in the README. For a fresh database, run bootstrap to create a confirmed administrator. Keep the private appsettings.json pointing to that test database.
2. Run `npm run dev --prefix AliceIdentityService.Admin`, then open https://localhost:7443/admin/users while signed out. Expect the approved direct login layout with a return URL.
3. Submit a wrong password, then the correct password. Expect the generic error with an empty password field, then the originally requested admin page. Its data and displayed administrator remain explicitly labeled samples.
4. Follow **Your account / sign out**. Confirm the actual account email, the administration link, and sign-out button. Sign out, then reopen an admin URL: login is required again. GET `/Account/Logout` must not sign out without submission.
5. On disposable accounts, check unconfirmed email, active lockout, and an authenticated account without `ais_admin`. The first two cannot sign in; the ordinary account can use `/Account` but not `/admin/` or `/api/system/info`. Automated tests cover these cases without modifying your accounts.
6. Inspect browser cookies with and without Remember me. Expect the security flags and persistence/expiry described above. Automated clock tests cover expiration without an eight-hour/fourteen-day wait.
7. Try `/Account/Login?returnUrl=https%3A%2F%2Fexample.org`. Successful login stays local. Confirm compact OIDC, registration, and recovery pages remain non-submitting previews.
8. Stop the dev runner with Ctrl+C. For the single-backend build, run `npm run build --prefix AliceIdentityService.Admin` then `dotnet run --project AliceIdentityService` and repeat the access checks. Stop that server too.

## Verification — 2026-11-14

- All 37 backend tests passed with PostgreSQL integration enabled, including the separate read-only AIS2 restore. Fresh databases used the versioned SQL scripts. The test copy contained no private appsettings or ignored migration sources.
- All 15 frontend tests passed (five existing UI tests and ten development authorization checks); formatting, lint, and the React build passed. .NET formatting verification passed.
- Browser verification over the actual HTTPS Vite/backend workflow confirmed anonymous admin deep-link challenge, successful login returning to the requested page, the signed-in account page, POST logout, and a new challenge after logout. The approved split account layout and React/Base UI preview rendered correctly.
- No schema, SQL, dependency-version, or private configuration changes. Verification used disposable databases; the configured AIS3 database was not changed, and AIS2 was only read for the test restore.
- Verification servers were stopped before handoff. Temporary databases and the temporary AIS2 dump were removed. Step 3 remains uncommitted for review; Step 4 has not started.
