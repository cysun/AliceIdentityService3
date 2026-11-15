# Step 4 review

Steps 1–3 are approved and committed. Step 4 is **uncommitted** for manual review. Step 5 has not started.

## Implemented behavior

- OpenIddict 7.5 server with discovery/JWKS and the existing `/connect/authorize`, `/connect/token`, `/connect/userinfo`, and `/connect/logout` paths. Authorization code and refresh are the only grant types. Public and confidential clients use their existing stored permissions, secrets, URIs, and PKCE settings; S256 is the only PKCE method.
- Compact server-rendered sign-in starts only after OpenIddict validates the authorization request. The displayed name comes from the registered client, is HTML-escaped, and is carried with a protected ten-minute continuation. Failed login and registration/recovery preview links retain that context. Expired/tampered context fails closed. Completing password login revalidates the protocol request against current client settings.
- Existing cookie authentication, account-state checks, safe redirects, approved account layouts, and administrator protection remain in place. `prompt=none`, forced login/account selection, and `max_age` use the actual authenticated session. Completing the requested password challenge removes only the satisfied challenge parameters from the protected continuation.
- Default lifetimes of five minutes for codes, one hour for signed access/ID tokens, and fourteen days for refresh tokens. Refresh tokens rotate, allow no reuse grace period, and keep their original expiry. Replay revokes the associated authorization. Code/refresh exchange checks the current security stamp, confirmation, lockout, two-factor state, account existence, and scope permissions; new tokens use current claims. Scope escalation is rejected and scope narrowing is supported.
- Userinfo requires a valid bearer token, validates stored token/authorization status, checks the account, and returns current scope-selected claims. Identity profile/custom claims follow the AIS2 contract. Security stamps are confined to encrypted grants, not exported in access/ID tokens or userinfo.
- RP-initiated logout validates the registered return URL and carries state through an antiforgery-protected confirmation. A GET or an unconfirmed RP POST does not clear the browser session. Logout does not revoke outstanding tokens or sign out other clients.
- CORS permits browser token/userinfo requests from registered client redirect origins without cookie credentials. Discovery/JWKS allow public GETs. Account pages and admin APIs have no cross-origin allowance. Vite forwards discovery as well as the existing backend routes.
- An explicit Development-only `review-client` command and a separate local `tools/OidcReviewClient` application support manual verification. The test client uses ASP.NET Core OIDC middleware for code + PKCE, state/nonce/signature validation, userinfo, and logout, plus a renewal button that validates the refreshed ID token. It is not part of the AIS3 solution or deployment.

AIS3 remains a single ASP.NET Core backend with Razor account pages and the React/Base UI admin SPA. Admin data is still fictional. Registration, confirmation, recovery, profile editing, and live administration remain later steps. Implicit consent is supported; clients requesting interactive consent are rejected rather than silently approved. Endpoint permissions remain ignored to match existing AIS2 records, while grant/response/scope permissions are enforced.

No model, migration, or SQL change is required. `CreateSchema.sql` and `PopulateSchema.sql` are unchanged. Private appsettings and migration sources remain ignored; the updated configuration sample is tracked. No automatic client seeding or schema update runs at web startup.

## Manual review

Use a **test database** initialized from the versioned SQL or a separate AIS2 restore as described in the [database guide](../database.md). Point private `AliceIdentityService/appsettings.json` at it. Use a confirmed test account; for a fresh database, bootstrap creates a confirmed administrator. Administration is not required for OIDC sign-in.

From the AIS3 repository root:

```sh
# Register the fixed localhost test client, then exit. Safe to repeat when it matches.
dotnet run --project AliceIdentityService -- review-client

# Build the SPA once, then run the single AIS3 backend (Rider also works).
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

The backend launch profile uses Development and HTTPS port 7443. Keep `OpenIddict:Issuer` at `https://localhost:7443/` for this test. If needed, trust the HTTPS certificate using `dotnet dev-certs https --trust`. Leave both token certificate paths empty to use OpenIddict development certificates; existing private configuration without an OpenIddict section also uses these defaults. Alternatively, run `npm run dev --prefix AliceIdentityService.Admin` for Vite/backend reload support. Run only one workflow on port 7443 at a time.

In a second terminal from the repository root:

```sh
dotnet run --project tools/OidcReviewClient
```

1. Open `https://localhost:9443/` in a private browser window, then choose **Sign in with AIS3**. Expect the approved compact login layout naming **AIS3 Review Client**. An existing AIS3 session can complete sign-in without showing the form; sign out of AIS3 first to inspect the form.
2. Try a wrong password, then the correct one. The error retains the client name and context and clears the password. On success, the review client displays the actual user and claims after validating the ID token. No raw tokens are displayed.
3. Choose **Renew tokens**. Expect a success message and a new access-token expiry. The client validates the new ID token and saves the rotated refresh token for the next renewal.
4. Choose **Sign out through AIS3**. Expect an AIS3 confirmation page; confirm sign-out to return to the review client's anonymous home. Repeating sign-in should require credentials again.
5. While signed out, visit `https://localhost:7443/Account/Login` directly. Expect the full direct-login layout. Sign in as an administrator and visit `/admin`; it serves the built React SPA with sample data. Sign-out and authorization checks from [Step 3](step-3.md) still apply.
6. Start another client login and follow **Create account** or **Forgot password?**. These remain non-submitting previews with the client context retained; returning to sign-in uses the same validated continuation. An expired continuation displays a restart message.
7. Inspect `https://localhost:7443/.well-known/openid-configuration`. Confirm the HTTPS issuer and all four endpoints. Invalid client/redirect/PKCE, expired context, token replay/revocation, and account-state cases are covered automatically below, without editing your test accounts by hand.
8. Stop both servers with Ctrl+C (or stop the backend in Rider). The `ais3-review` registration remains in the chosen test database for subsequent reviews. The command refuses to overwrite a client with different settings.

## Automated checks

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun' \
dotnet test
dotnet format --verify-no-changes --no-restore
dotnet build tools/OidcReviewClient
dotnet format tools/OidcReviewClient --verify-no-changes --no-restore
git diff --check
```

Adjust database host/user/name. The admin connection must create/drop disposable databases; tests do not mutate the configured application database. The restore connection must point to a separate AIS2 dump restore, prepared using the database guide; that check is read-only. The respective integration tests skip when these variables are absent. Build the frontend first so backend tests can check real admin assets. The review client builds separately because it is not an AIS3 service/solution project.

OIDC tests use real PostgreSQL stores, HTTP forms and antiforgery, password verification, signed/encrypted grants, discovery/JWKS, and ID-token signature/issuer/audience validation. They cover public PKCE and legacy confidential clients, POST authorization requests, scope claims/resources and narrowing, client/redirect changes during sign-in, login freshness, context expiry/tampering, bearer userinfo, revocation/replay, fixed refresh expiry, account-state/stamp changes, CORS, and logout confirmation/state/redirect protections.

## Verification — 2026-11-14

- All 50 backend tests passed with PostgreSQL and a separate read-only AIS2 restore enabled. Fresh test databases used the versioned SQL scripts, without ignored migration sources or private configuration.
- All 17 frontend tests passed; formatting, lint, and the React build passed. Both .NET projects built and formatting verification passed.
- Browser verification through the real HTTPS Vite/backend workflow completed compact sign-in, validated the ID token in the separate review client, renewed tokens, and returned after confirmed logout. The compact layout retained the approved design.
- The explicit review-client registration was repeated successfully without replacing the existing registration. No private configuration, AIS2 source, application database, schema, or SQL scripts were changed. AIS2 was only read to prepare the compatibility restore.
- Verification servers were stopped and disposable databases/dump removed before handoff. Step 4 remains uncommitted for review; Step 5 has not started.

## Limits retained for later review

The host is still Development/Testing-only. Production signing/encryption and Data Protection storage, key rotation, and AIS2 session/token continuity remain staging/migration decisions. Optional explicit token certificates must be configured as a pair and are separate from the HTTPS certificate. Independently validated access JWTs can remain valid until expiry after logout or account/stamp changes; this step does not add global session revocation, introspection, or front/back-channel logout. Existing deployed client credentials were not used in verification; representative real clients still require the migration rehearsal.
