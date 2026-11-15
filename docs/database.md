# Database scripts and compatibility

Version control contains two files under `AliceIdentityService/Scripts`:

- **CreateSchema.sql**: generated from the EF model using `dotnet ef migrations script`; creates a fresh database's tables, constraints, and model-defined indexes.
- **PopulateSchema.sql**: additional hand-written SQL when a feature requires it. It is currently empty. We will decide on search indexes/functions when implementing user search; AIS3 does not create AIS2's legacy search objects now.

`Migrations/` and its model snapshot are ignored local tooling artifacts. The application, bootstrap command, and tests do not require these files or compare migration IDs.

## Generate CreateSchema.sql

On a checkout with no local migrations, configure appsettings.json as described in the README, then run from `AliceIdentityService`:

```sh
dotnet ef migrations add InitialSchema
dotnet ef migrations script --output Scripts/CreateSchema.sql
```

Generating the script does not apply it to a database. If local migrations already exist, use/update that local migration set before exporting; do not add another migration with the same name. Regenerate and review CreateSchema.sql after model changes. Only the SQL is committed. Additional SQL belongs in PopulateSchema.sql, without adding `migrationBuilder.Sql()` calls to disposable migration code.

EF's generated script includes a `__EFMigrationsHistory` table and migration ID. We keep the generated output as-is. That metadata is not an application compatibility requirement and does not need to match AIS2 or another developer's locally generated ID.

## Initialize a fresh database

From the repository root, with PostgreSQL tools on PATH:

```sh
createdb -h /tmp ais3
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/CreateSchema.sql
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/PopulateSchema.sql
```

Use an empty database; skip createdb if it already exists. Adjust host/user/database for your environment. On this Mac the PostgreSQL tools are under `/Applications/Postgres.app/Contents/Versions/latest/bin` if not on PATH.

CreateSchema.sql is a full creation script, not an upgrade script or something to rerun over existing tables. Future changes to existing databases will need a reviewed SQL upgrade when the relevant feature is implemented. No automatic migration or schema repair runs during application startup.

Then point appsettings.json at this database and bootstrap its initial data:

```sh
dotnet run --project AliceIdentityService -- bootstrap --email admin@example.test --first-name First --last-name Last
```

Bootstrap prompts for a password and creates a confirmed administrator with the `ais_admin` claim and missing standard scopes. Optional `--screen-name` sets the display name; automation can use `--password-stdin`. Reruns preserve existing administrators and scopes, and existing non-admin accounts are not promoted. The command exits without starting the web server.

## AIS2 compatibility

An up-to-date AIS2 dump can be restored directly. Do not run CreateSchema.sql or PopulateSchema.sql over that restore, and do not alter its migration history. Its existing administrator/scopes normally make bootstrap unnecessary.

For a separate test copy:

```sh
mkdir -p .dev/db
umask 077
pg_dump -h /tmp -Fc --no-owner --no-privileges -f .dev/db/ais2.dump ais2
createdb -h /tmp -T template0 ais3_copy
pg_restore -h /tmp --no-owner --no-privileges --exit-on-error -d ais3_copy .dev/db/ais2.dump
```

User/string IDs, hashes, stamps, claims, client settings/secrets, scopes, authorizations, and tokens retain their AIS2 table/column mappings. CreationTime keeps its default; authorization Type is varchar(50), and token Type is varchar(150). A fresh AIS3 database has the same shared table structure and standard indexes. It intentionally omits AIS2's custom search function/expression indexes, which may remain in a restored dump without being required by AIS3.

Integration tests execute the actual versioned scripts on disposable databases and compare the shared schema/model mappings. Separate read-only tests validate an AIS2 restore. These tests also run without ignored migration files, and bootstrap is tested without a migration-history table. Steps 3 and 4 use those same tables for direct sign-in and OIDC; neither changes the model/schema or requires a SQL upgrade. Step 4 writes authorizations and tokens through OpenIddict during normal protocol use. Existing token rows do not establish usability under different signing/encryption keys; cutover key/session continuity remains deferred.

For manual Step 4 testing, the explicit Development-only `dotnet run --project AliceIdentityService -- review-client` command registers the public `ais3-review` client in the configured test database, then exits. Its fixed callback URLs use `https://localhost:9443`; it never runs at web startup and refuses to replace an existing client with different settings. See the [Step 4 checklist](reviews/step-4.md) before using it.

## Step 5 registration

Registration, confirmation, and resend use the existing Identity tables and require no schema/SQL upgrade. New users remain unconfirmed until a valid confirmation POST; confirmation and security-stamp rotation share one database transaction. Email publication follows account creation, so a failed publication leaves an unconfirmed account eligible for resend. There is no outbox table or automatic data cleanup in this step. Both versioned SQL scripts remain unchanged, and no migration source is needed.

## Step 6 recovery and profile

Password reset/change and profile name editing use the existing Identity user columns. Identity saves the password hash and security stamp in the same concurrency-checked update; a successful password operation invalidates older reset links, cookies, authorization codes, and refresh tokens. Profile edits retain the password, stamp, email, and claims and only change the current account's three name fields. There is no migration, schema regeneration, or SQL upgrade for Step 6. Both versioned scripts are unchanged.

## Step 7 administration overview

The session and overview endpoints read the existing Identity/OpenIddict mappings. Counts and bounded summaries share a repeatable-read transaction; they perform no writes or schema initialization. Administrator counts use distinct user accounts with an `ais_admin` claim, regardless of claim value. Lockout counts require `LockoutEnabled` and a future `LockoutEnd`. Recent registrations use `CreationTime` descending with ID ascending as a tie-breaker. No last-login/online-activity data is inferred. No migrations, SQL additions, or schema upgrades are needed; both versioned scripts remain unchanged.
