# Alice Identity Service 3

AIS3 is being rebuilt in reviewable steps. **Steps 1–3 are approved and committed. Step 4 is implemented and uncommitted for review. Step 5 has not started.** Direct account sign-in/out and OIDC authorization code, refresh, userinfo, and logout are connected. The approved admin UI still uses fictional sample data, behind administrator authorization.

## Projects

```text
AliceIdentityService3.sln
AliceIdentityService/          ASP.NET Core: account pages, OIDC/API host, models and services
  Controllers/
  Models/
  Services/
  Data/                       EF DbContext and service registration
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

Open https://localhost:7443/Account/Login or https://localhost:7443/admin/. The dev command starts both ASP.NET Core and Vite with reload support; Ctrl+C stops both. Initialize a fresh database with the SQL scripts below, or restore an AIS2 dump. Sign in with a confirmed existing account; the `ais_admin` claim is required for administration. `/Account` shows the signed-in session and a sign-out button. Vite checks access with the same backend policy before serving the admin preview.

Other previews: `/Account/Register`, `/Account/ForgotPassword`, `/admin/users`, and `/admin/components`. Registration and recovery remain non-submitting previews. Compact login now comes from a validated OIDC authorization request; `preview` and `app` query parameters cannot establish that context. See the [Step 4 review](docs/reviews/step-4.md) for a working test client.

## Account sessions

Direct sign-in requires a confirmed email and respects persisted lockout state. Five wrong passwords lock lockout-enabled accounts for five minutes. Failure messages do not distinguish unknown, wrong-password, locked, or unconfirmed accounts. Imported accounts with two-factor enabled cannot sign in until a challenge flow is implemented.

The `__Host-AIS3.Session` cookie is Secure, HttpOnly, SameSite=Lax, host-only, and scoped to `/`. Sessions have a fixed eight-hour expiry; **Remember me** persists the cookie for a fixed fourteen days. Requests never extend either expiry. Security stamps, confirmation, lockout, account existence, and current claims are checked on each authenticated request. Sign-out clears this browser's cookies; it does not revoke other browsers or OIDC tokens. Browser session restoration may retain nonpersistent cookies, so the server expiry remains authoritative.

Login and logout POSTs require antiforgery tokens. GET `/Account/Logout` only asks for confirmation. Return URLs must be local. The admin namespace, including built static assets, requires the existing AIS2 `ais_admin` claim. `/api/system/info` requires the same policy and returns 401/403 for unauthorized requests. The React shell still displays sample identity/data; live session handling and administration belong to later steps.

Use HTTPS, including in development. HTTP redirects to port 7443 by default (override with `HTTPS_PORT`); only health endpoints remain accessible over HTTP for the local runner. Only the local loopback proxy's forwarded scheme is trusted. Data Protection uses ASP.NET Core's existing local key storage defaults; production keys and AIS2 session/token continuity remain later review decisions. AIS3 uses its own cookie name and does not import AIS2 browser sessions.

## OIDC

Discovery at `/.well-known/openid-configuration` advertises `/connect/authorize`, `/connect/token`, `/connect/userinfo`, and `/connect/logout`. Registered clients can use authorization code and refresh-token grants. Client redirect/logout URLs, secrets, grant/response/scope permissions, and per-client PKCE requirements come from the existing OpenIddict tables. PKCE supports S256 only. AIS2's implicit consent setting is supported; clients needing an interactive consent screen are rejected until that feature is implemented. Endpoint permission checks remain disabled for compatibility with AIS2 client records, which do not contain those permissions.

Default token lifetimes are five minutes for authorization codes, one hour for access/ID tokens, and fourteen days for refresh tokens. Refresh tokens rotate on every successful use with no reuse grace period and retain their original expiry. Reusing a redeemed refresh token revokes its associated authorization. Code exchange and renewal check current account/security-stamp state and rebuild claims; changed client scope permissions also apply. Existing signed access JWTs can remain valid until expiry at independently validating APIs. Signing out clears the browser session, not outstanding tokens or other clients' sessions.

The compact Razor login uses the registered client name and a protected ten-minute continuation, preserving it through validation errors and preview links. Invalid or expired context requires restarting sign-in from the client. Logout requires confirmation and an antiforgery-protected POST before returning to a registered logout URL. Browser clients can call token/userinfo endpoints from registered redirect origins without CORS cookie credentials; account/admin endpoints do not allow cross-origin access.

`OpenIddict:Issuer` defaults to `https://localhost:7443/` and must match the public HTTPS origin used by clients, including through Vite. Development uses OpenIddict's persisted local signing/encryption certificates when both certificate paths are empty. To supply PKCS#12 certificates, configure **both** paths and their passwords in private configuration; relative paths resolve from the backend content root. These token certificates are separate from the HTTPS development certificate. Testing uses ephemeral keys. Production hosting/key management remains blocked until the hardening review.

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

If the empty database already exists, skip createdb. CreateSchema.sql is for an empty database and is not a repeatable upgrade script. PopulateSchema.sql is currently empty; future custom SQL belongs there. Bootstrap prompts for a password, creates a confirmed administrator and standard scopes, and is safe to repeat. It never promotes or resets an existing non-admin account.

For an **up-to-date AIS2 dump restore**, no bootstrap or data conversion is required. Keep its existing tables/data and skip both setup scripts. Migration IDs are irrelevant at runtime. See [database scripts and compatibility](docs/database.md) for the generation and restore workflow. Web startup never changes the schema. `/health` checks liveness; `/health/ready` checks that the users table can be queried.

## Build and test

```sh
npm run check --prefix AliceIdentityService.Admin
dotnet test
dotnet format --verify-no-changes --no-restore
```

The frontend check runs formatting, lint, build, UI tests, and development access checks. Build it before backend tests so HTTP tests can check real admin assets. PostgreSQL integration tests are opt-in; the [Step 4 checklist](docs/reviews/step-4.md) gives commands for running the full suite and manual protocol checks. The separate review client has its own build/format commands there.

To run the built UI using ASP.NET Core alone:

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

Stop the dev runner first because both use port 7443. Rider can run the same ASP.NET Core project after the React build: `/Account` serves Razor pages and `/admin` serves the compiled SPA from `wwwroot/admin`. The SPA is real React code; its administrative data is still a sample. The review client is a separate test application and is not part of the AIS3 solution or deployment. To publish AIS3, run `dotnet publish AliceIdentityService -c Release -o artifacts/preview` after the React build. Provision appsettings.json separately in that directory or use environment variables: configuration is excluded from publish output. The published application needs no Node runtime.

The web host remains Development/Testing-only until the staging and hardening review. See the [Step 4 review checklist](docs/reviews/step-4.md) and [implementation plan](docs/implementation-plan.md). Stop here for review before implementing Step 5.
