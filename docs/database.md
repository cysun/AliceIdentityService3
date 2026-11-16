# Database scripts and compatibility

Version control contains two files under `AliceIdentityService/Scripts`:

- **CreateSchema.sql**: generated from the EF model using `dotnet ef migrations script`; creates a fresh database's tables, constraints, and model-defined indexes.
- **PopulateSchema.sql**: additional hand-written SQL. Step 8 installs `pg_trgm` in `public` and a GIN expression index for user substring search. It is additive and rerunnable; AIS3 does not require or recreate AIS2's legacy search function/indexes.

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

An up-to-date AIS2 dump can be restored directly. Do not run CreateSchema.sql over that restore or alter its migration history. **Apply the reviewed Step 8 PopulateSchema.sql once after restoring** to add the search objects; rerunning it is safe. Its existing administrator/scopes normally make bootstrap unnecessary.

For a separate test copy:

```sh
mkdir -p .dev/db
umask 077
pg_dump -h /tmp -Fc --no-owner --no-privileges -f .dev/db/ais2.dump ais2
createdb -h /tmp -T template0 ais3_copy
pg_restore -h /tmp --no-owner --no-privileges --exit-on-error -d ais3_copy .dev/db/ais2.dump
psql -X -h /tmp -d ais3_copy -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/PopulateSchema.sql
```

User/string IDs, hashes, stamps, claims, client settings/secrets, scopes, authorizations, and tokens retain their AIS2 table/column mappings. CreationTime keeps its default; authorization Type is varchar(50), and token Type is varchar(150). A fresh AIS3 database has the same shared table structure and standard indexes. It omits AIS2's custom prefix-search function/expression indexes, which may remain in a restored dump without being required by AIS3. Step 8 adds its own index to either database; shared tables/columns and standard indexes are unchanged.

Integration tests execute the actual versioned scripts on disposable databases and compare the shared schema/model mappings. Separate read-only tests validate an AIS2 restore. These tests also run without ignored migration files, and bootstrap is tested without a migration-history table. Steps 3 and 4 use those same tables for direct sign-in and OIDC; neither changes the model/schema or requires a SQL upgrade. Step 4 writes authorizations and tokens through OpenIddict during normal protocol use. Existing token rows do not establish usability under different signing/encryption keys; cutover key/session continuity remains deferred.

For manual Step 4 testing, the explicit Development-only `dotnet run --project AliceIdentityService -- review-client` command registers the public `ais3-review` client in the configured test database, then exits. Its fixed callback URLs use `https://localhost:9443`; it never runs at web startup and refuses to replace an existing client with different settings. See the [Step 4 checklist](reviews/step-4.md) before using it.

## Step 5 registration

Registration, confirmation, and resend use the existing Identity tables and require no schema/SQL upgrade. New users remain unconfirmed until a valid confirmation POST; confirmation and security-stamp rotation share one database transaction. Email publication follows account creation, so a failed publication leaves an unconfirmed account eligible for resend. There is no outbox table or automatic data cleanup in this step. Both versioned SQL scripts remain unchanged, and no migration source is needed.

## Step 6 recovery and profile

Password reset/change and profile name editing use the existing Identity user columns. Identity saves the password hash and security stamp in the same concurrency-checked update; a successful password operation invalidates older reset links, cookies, authorization codes, and refresh tokens. Profile edits retain the password, stamp, email, and claims and only change the current account's three name fields. There is no migration, schema regeneration, or SQL upgrade for Step 6. Both versioned scripts are unchanged.

## Step 7 administration overview

The session and overview endpoints read the existing Identity/OpenIddict mappings. Counts and bounded summaries share a repeatable-read transaction; they perform no writes or schema initialization. Administrator counts use distinct user accounts with an `ais_admin` claim, regardless of claim value. Lockout counts require `LockoutEnabled` and a future `LockoutEnd`. Recent registrations use `CreationTime` descending with ID ascending as a tie-breaker. No last-login/online-activity data is inferred. No migrations, SQL additions, or schema upgrades are needed; both versioned scripts remain unchanged.

## Step 8 user search

Apply the versioned additive script explicitly to an existing AIS3 test database or a separate AIS2 restore:

```sh
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/PopulateSchema.sql
```

Use the database owner/deployment role, with permission to install `pg_trgm` and create an index on `public."AspNetUsers"`. The PostgreSQL installation must provide the extension. This script expects `pg_trgm` in `public`; review an installation in another schema before applying it. Run while the test application is stopped: ordinary CREATE INDEX blocks writes during construction. The script is transactional and uses IF NOT EXISTS for reruns; it does not repair a different object already using the same name. No schema work runs during startup or requests.

The only additions are:

- `pg_trgm`, including its PostgreSQL-managed extension functions/operators.
- `public."IX_AspNetUsers_Search"`, a nonunique GIN index with `gin_trgm_ops` on `"FirstName" || ' ' || "LastName" || ' ' || "ScreenName" || ' ' || COALESCE("Email", '')`.

The EF query uses that exact expression with parameterized `ILIKE` and an explicit backslash escape. PostgreSQL maintains the expression index when a name/email changes; there is no generated column, trigger, custom search function, or new model property. `CreateSchema.sql` stays unchanged, and EF migration source stays ignored. Legacy AIS2 search objects are retained; removal is outside this step.

The extension/index improve selective substring searches. Short/common patterns or small tables may correctly use sequential scans. Sorting/counting still have costs: the GIN index does not provide name/status/date ordering. Step 8 does not add speculative indexes on every sort/filter field. Existing normalized email/username and primary-key indexes continue serving exact Identity lookups.

Check the installation:

```sql
SELECT extname, extversion FROM pg_extension WHERE extname = 'pg_trgm';
SELECT i.indexdef, x.indisvalid
FROM pg_indexes i
JOIN pg_class c ON c.relname = i.indexname AND c.relnamespace = 'public'::regnamespace
JOIN pg_index x ON x.indexrelid = c.oid
WHERE i.schemaname = 'public' AND i.indexname = 'IX_AspNetUsers_Search';
```

The integration suite applies both scripts on fresh disposable databases, reapplies PopulateSchema.sql, and checks the actual EF-generated count/page query plans against 30,000 synthetic users with EXPLAIN (ANALYZE, BUFFERS). It verifies that selective search uses the GIN index without disabling sequential scans, and exercises common/short search and a deep page. AIS2 restore checks remain read-only after the separately applied setup script. The schema comparison excludes extension-owned functions and the explicitly tested additive index while continuing to compare the shared AIS2 contract.

## Step 9 administrative account operations

Step 9 uses the existing Identity and OpenIddict mappings; both SQL scripts remain unchanged. Name edits automatically update the Step 8 GIN expression index. The detail API exposes a SHA-256-derived edit version from the user ID and concurrency stamp, never the raw stamp; Identity concurrency checks and submitted versions prevent overwriting normal intervening updates. This version is a concurrency marker, not an authorization credential.

Administrative writes use a database transaction and the transaction-scoped advisory lock `7193402009`, then reload/revalidate the actor's account, security stamp and current `ais_admin` claim. Serializing these low-volume writes across instances prevents simultaneous administrators disabling each other with already-admitted requests. Step 10 claim/scope and Step 11 client writes use the same serialization boundary through the shared AdminWriteGuard. Ordinary reads and sign-ins are not serialized by this lock.

Confirmation changes, password resets and lock/unlock rotate the security stamp and revoke all subject tokens/authorizations through the same scoped DbContext and transaction. Deletion cascades through the existing Identity foreign keys and revokes the subject's OIDC records; protocol records remain for normal OpenIddict retention. If revocation fails, Identity changes and any preceding revocations roll back. No migration, new columns, trigger, outbox, audit table or schema initialization is added. Success logs record actor/target IDs and operation; durable audit history remains a later operational concern.

## Step 10 claims and scopes

Steps 1–10 are approved and committed; Step 10 is `5b469b0`. No model/schema/SQL changes are required. Private appsettings.json and migration code remain ignored; appsettings.sample.json remains tracked. CreateSchema.sql and PopulateSchema.sql are unchanged.

Assignments use existing `AspNetUserClaims` rows and expose their integer ID for exact-row removal, including imported duplicates and null values. New exact duplicates are rejected under the shared administrative transaction lock; no unique index is added to incompatible legacy data. Adding/removing a row, rotating the user's stamps and revoking subject OIDC records commit or roll back together. Ordinary Identity concurrency checks still reject intervening account changes.

Custom definitions use `OpenIddictScopes`, with claim types serialized into the existing `Properties` object's `claims` array and resources in the existing resources field. OpenIddict managers validate/save definitions and maintain their concurrency tokens. The API exposes a derived edit version, not raw tokens or unrelated properties. Edits populate an existing descriptor before replacing only the supported fields, retaining localized names/descriptions and other properties. No standard definition is overwritten by administration. Scope edits do not invalidate user cookies/grants; subsequent issuance and userinfo use the updated definition. Existing JWTs retain their original contents.

Names are stable identifiers. Before deleting a custom definition, the service checks exact client scope permissions and valid authorizations using the OpenIddict managers. No client, authorization or token rows are rewritten by scope CRUD. Active grants must first be revoked and client permissions removed in their owning workflows; Step 11 client settings now provide this permission removal and revoke that client's grants. These application-level checks cover AIS3 administrative writes; direct SQL or a simultaneously running AIS2 writer does not participate in the lock. Shared-database concurrent administration remains outside the approved development workflow.

## Step 11 clients

Client administration uses the existing `OpenIddictApplications` fields and OpenIddict managers. No model, schema, SQL or migration changes are required. CreateSchema.sql remains the generated fresh schema; PopulateSchema.sql retains only the approved additive search setup. Private appsettings.json/migrations remain ignored and appsettings.sample.json remains tracked.

Application reads expose a safe projection and an opaque version derived from the record ID/concurrency token. They never return the secret hash or arbitrary properties. Updates populate the existing descriptor and replace only supported fields, preserving other permissions/requirements, localized names, properties, settings and application/consent type. A normal edit preserves the existing secret hash; credential changes are hashed by OpenIddict. Existing imported callbacks may remain unchanged, including custom native schemes; new callbacks follow the supported web URL rules. Unsupported imported client/consent/flow profiles remain read-only.

Changes to callbacks, allowed scopes, refresh permission, PKCE or credentials revoke tokens and authorizations by application ID in the same shared transaction. Display-name-only edits do not revoke grants. Client deletion uses the OpenIddict manager to remove the application and associated authorizations/tokens; reuse of the public client ID cannot revive old grants. Injected revocation/deletion failures are tested for rollback. No Identity cookie/security stamp changes occur. Already-issued self-contained JWTs and downstream sessions retain their own expiry.

The shared administrative lock also serializes scope-deletion checks with client permission changes. OpenIddict entity caching remains disabled across instances, so new credentials, callback CORS origins and permissions become visible without restarting. Direct SQL or concurrent AIS2 writers do not participate in this lock. See the [Step 11 review](reviews/step-11.md); Step 11 is approved and committed as `07e10f0`.

## Step 12 mail publication

Step 12 adds no tables, model changes, migrations or SQL. Confirmation/resend and password recovery retain their existing account, token and throttling behavior. Failed publication does not remove a newly created unconfirmed account; a later request can generate another link under the same limits.

AIS3's responsibility ends at RabbitMQ publication. The consumer's SMTP delivery, retries and failed-message storage are outside AIS3 and its database. No outbox, delivery ledger, queue diagnostics or consumer-management feature is added. Tests use isolated temporary RabbitMQ queues and no mail consumer or SMTP server.

CreateSchema.sql and PopulateSchema.sql remain unchanged. Private appsettings.json and migration code remain ignored; appsettings.sample.json remains tracked. See the [Step 12 review](reviews/step-12.md). Step 12 is approved and committed as `954b5bd`.

## Step 13 staging and hardening

Step 13 introduces no schema, model or SQL changes. Deployed hosts validate configuration and persistent key storage; database readiness reads the existing user table. Startup never applies schema scripts, migrations or bootstrap. The runtime database role needs ordinary table/sequence access, while schema setup uses a separate owner/deployment role.

Staging uses its own fresh database or separate AIS2 restore, with PopulateSchema.sql applied explicitly. The two passwordless certificates and their `keys` subfolder for Data Protection are stored together in `Application:CertificateFolder`, outside release directories and the database; preserve the entire folder across deployments and back it up with the private configuration and database. Code rollback retains the current database and key material, avoiding loss of intervening security changes. AIS2 token/session continuity and migration remain Step 14 work. See the [deployment guide](deployment.md) and [Step 13 review](reviews/step-13.md).
