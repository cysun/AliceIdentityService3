# Alice Identity Service 3

AIS3 is being rebuilt in reviewable steps. **Steps 1–9 are approved and committed (Step 9: `eb775d0`). Step 10 is implemented and uncommitted for review. Step 11 has not started.** Direct/OIDC sign-in, registration, email confirmation, password recovery/change, profile editing, and basic mail delivery integration are connected. The admin shell shows the current administrator and real overview data. User search, pagination, filtering, details, account operations, claim assignment and scope definitions use the database.

## Projects

```text
AliceIdentityService3.sln
AliceIdentityService/          ASP.NET Core: account pages, OIDC/API host, models and services
  Controllers/
  Models/
  Services/
  Data/                       EF DbContext and service registration
  EmailTemplates/             Versioned email subjects and plain-text/HTML bodies
  Scripts/                    Versioned CreateSchema.sql and PopulateSchema.sql
  Views/
  wwwroot/
  Program.cs
  appsettings.sample.json
AliceIdentityService.Admin/    React, Base UI, Vite, and all npm configuration
AliceIdentityService.Tests/    HTTP and PostgreSQL integration tests
tools/OidcReviewClient/        Standalone local OIDC client for manual review only
```

One ASP.NET Core process serves Razor account pages, the admin API, and built React assets. Account pages load their small native script and CSS; React is loaded only for `/admin/`. There is no separate Core or Console project. Bootstrap is a command mode of the same application.

## Setup and run

Requirements: .NET SDK 10.0.404 (or a later 10.0.4xx patch), Node.js 24+, PostgreSQL, and the EF Core 10 CLI when regenerating schema SQL. Run these commands from the repository root:

```sh
# Keep an existing local configuration:
cp -n AliceIdentityService/appsettings.sample.json AliceIdentityService/appsettings.json
# Edit the PostgreSQL connection and Serilog file path in appsettings.json.
npm ci --prefix AliceIdentityService.Admin
dotnet restore
# Once per machine if the development certificate is not trusted:
dotnet dev-certs https --trust
npm run dev --prefix AliceIdentityService.Admin
```

Open https://localhost:7443/Account/Login or https://localhost:7443/admin/. The dev command starts both ASP.NET Core and Vite with reload support; Ctrl+C stops both. Initialize a fresh database with the SQL scripts below, or restore an AIS2 dump. Sign in with a confirmed existing account; the `ais_admin` claim is required for administration. `/Account` (also `/Account/Profile`) shows your profile, editable names, password-change link, and sign-out button. Vite checks access with the same backend policy before serving the admin UI.

Registration at `/Account/Register` and recovery at `/Account/ForgotPassword` are functional. The overview at `/admin/` and user directory at `/admin/users` are live; `/admin/components` remains a local component preview. Compact login now comes from a validated OIDC authorization request; `preview` and `app` query parameters cannot establish that context. See the [Step 4 review](docs/reviews/step-4.md) for a working test client.

## Account sessions

Direct sign-in requires a confirmed email and respects persisted lockout state. Five wrong passwords lock lockout-enabled accounts for five minutes. Failure messages do not distinguish unknown, wrong-password, locked, or unconfirmed accounts. Imported accounts with two-factor enabled cannot sign in until a challenge flow is implemented.

The `__Host-AIS3.Session` cookie is Secure, HttpOnly, SameSite=Lax, host-only, and scoped to `/`. Sessions have a fixed eight-hour expiry; **Remember me** persists the cookie for a fixed fourteen days. Requests never extend either expiry. Security stamps, confirmation, lockout, account existence, and current claims are checked on each authenticated request. Sign-out clears this browser's cookies; it does not revoke other browsers or OIDC tokens. Browser session restoration may retain nonpersistent cookies, so the server expiry remains authoritative.

Login and logout POSTs require antiforgery tokens. GET `/Account/Logout` only asks for confirmation. Return URLs must be local. The admin namespace, including built static assets, requires the existing AIS2 `ais_admin` claim. `/api/system/info` requires the same policy and returns 401/403 for unauthorized requests. The React shell loads the current administrator from `/api/admin/session` before displaying protected routes. Sign-out uses the existing Razor confirmation and antiforgery-protected POST.

Use HTTPS, including in development. HTTP redirects to port 7443 by default (override with `HTTPS_PORT`); only health endpoints remain accessible over HTTP for the local runner. Only the local loopback proxy's forwarded scheme is trusted. Data Protection uses ASP.NET Core's existing local key storage defaults; production keys and AIS2 session/token continuity remain later review decisions. AIS3 uses its own cookie name and does not import AIS2 browser sessions.

## OIDC

Discovery at `/.well-known/openid-configuration` advertises `/connect/authorize`, `/connect/token`, `/connect/userinfo`, and `/connect/logout`. Registered clients can use authorization code and refresh-token grants. Client redirect/logout URLs, secrets, grant/response/scope permissions, and per-client PKCE requirements come from the existing OpenIddict tables. PKCE supports S256 only. AIS2's implicit consent setting is supported; clients needing an interactive consent screen are rejected until that feature is implemented. Endpoint permission checks remain disabled for compatibility with AIS2 client records, which do not contain those permissions.

Default token lifetimes are five minutes for authorization codes, one hour for access/ID tokens, and fourteen days for refresh tokens. Refresh tokens rotate on every successful use with no reuse grace period and retain their original expiry. Reusing a redeemed refresh token revokes its associated authorization. Code exchange and renewal check current account/security-stamp state and rebuild claims; changed client scope permissions also apply. Existing signed access JWTs can remain valid until expiry at independently validating APIs. Signing out clears the browser session, not outstanding tokens or other clients' sessions.

The compact Razor login uses the registered client name and a protected ten-minute continuation, preserving it through validation errors, registration, and password recovery. Invalid or expired context requires restarting sign-in from the client. Logout requires confirmation and an antiforgery-protected POST before returning to a registered logout URL. Browser clients can call token/userinfo endpoints from registered redirect origins without CORS cookie credentials; account/admin endpoints do not allow cross-origin access.

`OpenIddict:Issuer` defaults to `https://localhost:7443/` and must match the public HTTPS origin used by clients, including through Vite. Development uses OpenIddict's persisted local signing/encryption certificates when both certificate paths are empty. To supply PKCS#12 certificates, configure **both** paths and their passwords in private configuration; relative paths resolve from the backend content root. These token certificates are separate from the HTTPS development certificate. Testing uses ephemeral keys. Production hosting/key management remains blocked until the hardening review.

## Registration and email confirmation

Registration preserves the approved full or compact Razor layout. New accounts require email confirmation and receive no administrative claims. Duplicate registration never overwrites a password or profile. Password requirements use the existing Identity defaults: at least six characters with uppercase/lowercase letters, a digit, and a symbol.

Confirmation links expire after 24 hours. Opening a link displays a confirmation form; its antiforgery-protected POST confirms the email and rotates the security stamp atomically. Used/expired/invalid links are rejected. Confirmation does not sign the browser in: the user enters their password afterward. Valid OIDC context continues to the client; if the ten-minute sign-in context expires first, confirmation still works and the user is asked to restart sign-in from their application.

`/Account/ResendConfirmation` requests another email. Normal registration/resend results use a generic message for new, existing, or unknown addresses. Registration, resend, and password recovery share a limit of ten POSTs per IP per fifteen minutes (HTTP 429); each normalized recipient is limited to one email attempt per minute and five per hour. These bounded, in-process recipient limits reset when the service restarts; distributed limits remain a deployment concern.

Configure the `Email` and `RabbitMQ` sections from `appsettings.sample.json` in your private configuration. `Email:AppUrl` is the public HTTPS origin used in links (default `https://localhost:7443/`); it does not come from the request Host header. Set a sender address accepted by your mail provider. RabbitMQ host, credentials, and queue must match the AliceMailService consumer; AIS3 also supports explicit port, virtual host, and TLS settings for the publisher. SMTP configuration belongs to AliceMailService, not AIS3.

AIS3 publishes a persistent, mandatory MessagePack list of MIME-message byte arrays to the durable queue and awaits broker confirmation. AliceMailService sends the email. A queue/configuration failure returns HTTP 503 with a retry page; a newly created account remains unconfirmed and can request another email after the cooldown. Broker acceptance is not proof of mailbox delivery, and a timeout can leave publication uncertain, so retry may produce a duplicate email. There is no automatic application retry or database outbox in Step 5. Application mail logs omit recipients, tokens, links, and raw exception text.

Email content is kept in `AliceIdentityService/EmailTemplates`: `EmailVerification.Subject.txt`, `EmailVerification.Body.txt` (plain text), and `EmailVerification.Body.html`. The bodies use the literal `{{link}}` placeholder; HTML substitutions are escaped. Files are read when sending and copied into build/publish output, so copy edits do not require changing C# code. Missing or invalid templates follow the same retry/failure path as a mail-publication failure. Password-reset content uses the corresponding `ResetPassword.Subject.txt`, `.Body.txt`, and `.Body.html` files through the same renderer.

The [Step 5 review instructions](docs/reviews/step-5.md) cover configuration, direct/OIDC registration, resend, and failure cases. Basic delivery integration moved forward from Step 12; password recovery reuses it, and Step 12 retains the operations review.

## Password recovery and profile

`/Account/ForgotPassword` sends a reset link only for a unique, confirmed email address on an account with a password. Normal responses are identical for eligible, unknown, unconfirmed, ambiguous, throttled, and mail-failure cases. A failed publication is logged without recipient/link/token details; wait at least one minute before retrying. The same IP/recipient limits used by registration apply. Recovery does not change the password or sign anyone out until a reset is submitted successfully.

Reset links expire after one hour and use a separate token purpose from email confirmation. GET displays the form; the antiforgery-protected POST validates the new password and changes the hash/security stamp together. A successful reset invalidates every outstanding reset link, existing AIS browser sessions, and earlier authorization codes/refresh tokens. Concurrent uses cannot both succeed. Password change at `/Account/ChangePassword` requires an authenticated session and the current password; wrong current passwords use the existing account lockout policy. Both successful operations require signing in again, including in the current browser. Neither clears lockout or disables two-factor protection. Already-issued access/ID JWTs and client-local sessions may remain usable until their own expiry; offline APIs cannot observe a changed Identity security stamp.

A valid OIDC continuation survives recovery and resumes only after sign-in with the new password. An expired continuation does not prevent a password reset; restart sign-in from the application afterward.

The authenticated profile at `/Account` or `/Account/Profile` edits first name, last name, and screen name (required, up to 255 characters each). The confirmed sign-in email is read-only, matching the approved mockup. Updates affect only the current user and cannot change email, passwords, stamps, claims, or permissions through submitted extra fields. Names are trimmed and HTML-escaped when displayed. Profile changes do not extend the fixed session expiry; subsequent userinfo responses and newly issued/refreshed ID tokens use the current names. No database or SQL changes are required.

See the [Step 6 review instructions](docs/reviews/step-6.md) for manual recovery, password-change, profile, OIDC, and failure checks.

## Live administration shell

`/admin/` displays the signed-in administrator's screen name and email, with links to the account page and sign-out confirmation. All admin HTML/assets and `/api/admin/session` / `/api/admin/overview` require the existing `ais_admin` policy. Anonymous API calls return 401 and non-admin calls return 403, without HTML redirects. Responses are not cached. The SPA uses the existing same-origin cookie and does not use localStorage or sessionStorage for credentials or data.

The session response contains only the current user's ID, email and names, the original cookie expiry, server time, and application version. The SPA checks access before rendering its shell, on navigation, focus/page restore/visibility changes, and every minute while visible. A timer uses the server-provided remaining lifetime. These checks do not extend the fixed eight-hour/fourteen-day cookie expiry. A rejected session or API request clears protected content and offers sign-in or explains the missing permission. Network/server failures show a retry state; a changed signed-in user reloads the page data.

The overview uses one consistent database snapshot for total users, registered applications, unconfirmed emails, distinct accounts assigned `ais_admin`, and enabled/unexpired lockouts. Administrator counts include assigned claims regardless of their value or the account's current lockout/confirmation state. Recent registrations show at most five accounts, ordered by creation time descending and ID ascending for ties. Application cards show at most six registered clients, ordered by display name (falling back to client ID/record ID). **Refresh** updates the snapshot; session polling does not refresh the overview. A registered client is not a measurement of online activity.

Only safe summary fields are projected into the overview. No password hashes, security/concurrency stamps, client secrets, arbitrary claims, or client properties are serialized. Recent rows open live user details, and attention links open filtered user lists. Clients remain a planned page, and component demonstrations remain local previews.

See the [Step 7 review instructions](docs/reviews/step-7.md) for counts, session expiry/sign-out, access denial, failure recovery, and regression checks. Step 7 itself needed no schema changes. Step 8 adds the search objects described below.

## User directory and search

`/admin/users` loads `/api/admin/users`; `/admin/users/{id}` loads the matching read-only detail endpoint. Both require the current `ais_admin` claim and use the existing session/failure boundary. Responses are not cached. No administrative mutation endpoints are added in Step 8.

Search is case-insensitive substring matching across first name, last name, screen name, and email. Every whitespace-separated term must match somewhere, in any order. `%`, `_`, and backslash are literal characters; accents are not folded and typos are not corrected. Short and Unicode names remain searchable. Input is limited to 256 characters and eight distinct terms. Typing is debounced by 300 ms, cancels obsolete requests, and preserves input focus.

Search, status, sorting, page, and page size are stored in the URL. Sorting and pagination run on the server; no whole-directory download is used. Default order is display name (case-insensitive, falling back to first/last name); User, Status and Joined headers support ascending/descending order with ID ascending as the final tie-breaker. Pages default to 25 users, with 50/100 also offered; the API accepts 1–100 per page and pages 1–10,000. Pages beyond the current result set return its last page. Filter/sort/page-size changes reset paging, and detail links preserve the list URL.

Confirmed/Unconfirmed filter the email-confirmation flag, including accounts that are locked; Locked requires enabled, unexpired lockout. The badge gives active lockout precedence over email-confirmation status. These definitions match the overview's attention counts. Counts and page results share a repeatable-read snapshot. Refresh explicitly reloads the current view.

Details show names, email/confirmation, creation time, administrator membership, stored claim type/value pairs, and password-presence/two-factor/lockout summaries. Claim text is escaped. Identity entities, password hashes, security/concurrency stamps and login/token secrets are never serialized. Details include an opaque edit version; raw concurrency stamps remain private. The Claims tab supports the Step 10 assignment/removal operations described below.

**Apply `Scripts/PopulateSchema.sql` if your test database does not already have the approved Step 8 search index.** It installs `pg_trgm` and one GIN expression index without changing Identity tables. See the [Step 8 review instructions](docs/reviews/step-8.md) for database setup, API/query limits, browser checks, and automated tests. Step 9 adds no SQL or schema changes.

## Administrative account operations

Step 9 connects the existing Base UI dialogs to protected write endpoints. Administrators can create confirmed ordinary accounts, edit first/last/screen names and email confirmation, reset passwords, lock for one hour/24 hours/until unlocked, clear lockout and failed attempts, and permanently delete accounts. Email addresses stay read-only after creation; clients and two-factor configuration keep their later review gates. Account creation and administrator password reset send no email, matching AIS2's administrative workflow.

Every write requires the current administrator session and an antiforgery token. Forms carry an opaque version from the latest detail read; stale edits return 409 and require reload. Names-only updates retain sessions. Confirmation changes, password resets, lock/unlock and deletion invalidate AIS cookies and OIDC codes/refresh tokens, revoke persisted tokens/authorizations, and leave independently validated JWTs subject to their existing expiry. Identity writes and revocation share a transaction. The currently signed-in administrator cannot lock/delete/unconfirm their own account; an own-password reset ends their session.

Dialogs identify the target and explain the effect before submission. Deletion also requires typing the email address (or ID for an account without email). Failed validation stays in the form; uncertain network/server failures clear protected data and require reloading the current account state, without replaying a write. Successful operations are logged with actor ID, operation and target ID, never submitted passwords or reset tokens.

See the [Step 9 review instructions](docs/reviews/step-9.md) for the operation matrix, API, automated checks and manual tests.

## Claims and scopes

Step 10 connects **Users → Claims** and `/admin/scopes`. Claim assignment adds a type/value pair; removal targets a stored row so imported duplicate/null claims can be removed individually. Types and values are trimmed, types are case-sensitive, and exact duplicate pairs are rejected. Empty values are supported. Identity-generated and private protocol claims cannot be assigned. Any `ais_admin` value grants full administration access; the current administrator cannot remove their own last such claim.

Claim changes rotate the security stamp and revoke subject tokens/authorizations in the same transaction. The account must sign in again, including the current administrator when editing their own claims. Earlier confirmation/recovery links also become invalid. Existing independently validated JWTs retain their claims until expiry.

Scopes use AIS2's OpenIddict `Properties["claims"]` array and resource identifiers. The directory supports server-side search and paging. Custom scopes can be created, edited and deleted; names stay fixed after creation. Display name, description, claim types and resources are editable. Standard scopes are read-only because their token mappings are fixed by the existing protocol contract. Deletion requires the exact scope name and is refused while a registered client permits the scope or a valid authorization references it. Client permission editing remains Step 11.

Scope edits preserve unrelated imported properties and localized names/descriptions. They apply on subsequent token issuance/refresh and userinfo requests; they do not revoke existing grants or alter already-issued JWTs. Only matching user claims are released when the client is permitted to request the scope and the scope is granted. Resources become access-token audiences. Private security/protocol claims never receive public token destinations, including when imported definitions list them. Standard scope claim destinations remain unchanged.

All writes use the existing administrator, antiforgery, edit-version and serialized actor checks. Success logs contain actor ID, operation and target ID, not claim values. No configuration, dependencies, SQL or schema changes are needed. See the [Step 10 review](docs/reviews/step-10.md) for the API, limits, token behavior and manual testing instructions. **Stop for manual review before Step 11.**

## Configuration and database

ASP.NET Core loads appsettings.json, optional environment-specific files, environment variables, and command-line overrides normally. The real files are ignored; the sample is tracked. Both the web host and bootstrap use the same configuration. Set the connection under `ConnectionStrings:DefaultConnection`, and the daily log filename under `Serilog:WriteTo:1:Args:path`. Relative log paths use the process working directory. Database settings never go into the React project.

For a **new database**, create it and apply the versioned scripts (adjust host/user as needed):

```sh
createdb -h /tmp ais3
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/CreateSchema.sql
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/PopulateSchema.sql
# Point appsettings.json at this database, then create its administrator:
dotnet run --project AliceIdentityService -- bootstrap --email admin@example.test --first-name First --last-name Last
```

If the empty database already exists, skip createdb. CreateSchema.sql is for an empty database and is not a repeatable upgrade script. PopulateSchema.sql installs the additive Step 8 search extension/index and is safe to rerun. Bootstrap prompts for a password, creates a confirmed administrator and standard scopes, and is safe to repeat. It never promotes or resets an existing non-admin account.

For an **up-to-date AIS2 dump restore**, no bootstrap or data conversion is required. Keep its existing tables/data, skip CreateSchema.sql, and explicitly apply PopulateSchema.sql for the Step 8 search index. Migration IDs are irrelevant at runtime. See [database scripts and compatibility](docs/database.md) for the generation and restore workflow. Web startup never changes the schema. `/health` checks liveness; `/health/ready` checks that the users table can be queried.

## Build and test

```sh
npm run check --prefix AliceIdentityService.Admin
dotnet test
dotnet format --verify-no-changes --no-restore
```

The frontend check runs formatting, lint, build, UI tests, and development access checks. Build it before backend tests so HTTP tests can check real admin assets. PostgreSQL integration tests are opt-in; the [Step 10 checklist](docs/reviews/step-10.md) gives commands for running the full suite and manual administration checks. The separate review client is documented in the [Step 4 checklist](docs/reviews/step-4.md).

To run the built UI using ASP.NET Core alone:

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

Stop the dev runner first because both use port 7443. Rider can run the same ASP.NET Core project after the React build: `/Account` serves Razor pages and `/admin` serves the compiled SPA from `wwwroot/admin`. The SPA is real React code; its identity, overview, user directory, details, account operations, claims and scopes are live. The review client is a separate test application and is not part of the AIS3 solution or deployment. To publish AIS3, run `dotnet publish AliceIdentityService -c Release -o artifacts/preview` after the React build. Provision appsettings.json separately in that directory or use environment variables: configuration is excluded from publish output. The published application needs no Node runtime.

The web host remains Development/Testing-only until the staging and hardening review. See the [Step 10 review checklist](docs/reviews/step-10.md) and [implementation plan](docs/implementation-plan.md). Stop here for review before implementing Step 11.
