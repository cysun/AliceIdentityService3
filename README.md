# Alice Identity Service 3

AIS3 is being rebuilt in reviewable steps. **Steps 1 and 2 are approved and committed. Step 3 is implemented and uncommitted for review. Step 4 has not started.** Direct account sign-in/out is connected. The approved admin UI still uses fictional sample data, behind administrator authorization.

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

Other previews: `/Account/Register`, `/Account/ForgotPassword`, `/Account/Login?preview=oidc&app=campus-portal`, `/admin/users`, and `/admin/components`. OIDC query parameters are visual preview controls until Step 4. Only the direct login form submits credentials; registration, recovery, and the compact OIDC form remain non-submitting previews.

## Account sessions

Direct sign-in requires a confirmed email and respects persisted lockout state. Five wrong passwords lock lockout-enabled accounts for five minutes. Failure messages do not distinguish unknown, wrong-password, locked, or unconfirmed accounts. Imported accounts with two-factor enabled cannot sign in until a challenge flow is implemented.

The `__Host-AIS3.Session` cookie is Secure, HttpOnly, SameSite=Lax, host-only, and scoped to `/`. Sessions have a fixed eight-hour expiry; **Remember me** persists the cookie for a fixed fourteen days. Requests never extend either expiry. Security stamps, confirmation, lockout, account existence, and current claims are checked on each authenticated request. Sign-out clears this browser's cookies; it does not revoke other browsers or OIDC tokens. Browser session restoration may retain nonpersistent cookies, so the server expiry remains authoritative.

Login and logout POSTs require antiforgery tokens. GET `/Account/Logout` only asks for confirmation. Return URLs must be local. The admin namespace, including built static assets, requires the existing AIS2 `ais_admin` claim. `/api/system/info` requires the same policy and returns 401/403 for unauthorized requests. The React shell still displays sample identity/data; live session handling and administration belong to later steps.

Use HTTPS, including in development. HTTP redirects to port 7443 by default (override with `HTTPS_PORT`); only health endpoints remain accessible over HTTP for the local runner. Only the local loopback proxy's forwarded scheme is trusted. Data Protection uses ASP.NET Core's existing local key storage defaults; production keys and AIS2 session/token continuity remain later review decisions. AIS3 uses its own cookie name and does not import AIS2 browser sessions.

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

The frontend check runs formatting, lint, build, UI tests, and development access checks. Build it before backend tests so HTTP tests can check real admin assets. PostgreSQL integration tests are opt-in; the [Step 3 checklist](docs/reviews/step-3.md) gives commands for running the full suite and manual account/access checks.

To run the built UI using ASP.NET Core alone:

```sh
npm run build --prefix AliceIdentityService.Admin
dotnet run --project AliceIdentityService
```

Stop the dev runner first because both use port 7443. To publish, run `dotnet publish AliceIdentityService -c Release -o artifacts/preview` after the React build. Provision appsettings.json separately in that directory or use environment variables: configuration is excluded from publish output. The published application needs no Node runtime.

The web host remains Development/Testing-only until the staging and hardening review. See the [Step 3 review checklist](docs/reviews/step-3.md) and [implementation plan](docs/implementation-plan.md). Stop here for review before implementing Step 4.
