# AIS2 migration rehearsal

Step 14 rehearses a direct PostgreSQL dump/restore into isolated databases. It does not cut over production, change AIS2, convert account data, or share a database between running AIS2/AIS3 writers. Steps 1–14 are reviewed and committed; Step 14 is `f7c59f8`. The [Step 15 runbook](cutover.md) prepares the release and instructions for manual deployment.

## Data contract

Restore the complete AIS2 database into an empty database with `pg_restore`. Do **not** apply CreateSchema.sql or bootstrap over the restore. Apply only the reviewed PopulateSchema.sql to add the search index/extension. No new SQL or model changes are needed for Step 14. Generated schema remains in CreateSchema.sql, additional SQL in PopulateSchema.sql, and migration source/private settings remain ignored.

The rehearsal compares all public tables, including `__EFMigrationsHistory`, before any sign-in. Each table gets a row count and SHA-256 digest of all columns in canonical JSON order, with length-prefixed rows and UTC timestamps. This includes user IDs, profile fields, password hashes, security/concurrency stamps, claims, client secret hashes/permissions/callbacks, scope properties/resources, authorizations, token payloads and historical migration metadata. The report contains aggregate digests, never individual rows or hashes. Identity can upgrade an old password hash and rotate its security stamp on a later successful sign-in; this is normal application activity, not a migration transformation.

The shared public catalog, legacy search objects, sequence values and `is_called` state must also match. PopulateSchema.sql is applied twice to demonstrate rerunnability without changing data. AIS3 model/store checks parse the restored client and scope settings. Missing/incompatible schema, malformed settings, count/content/sequence differences and tool warnings fail verification; they are never automatically repaired.

## Run the repeatable rehearsal

Requirements: the repository's .NET SDK, a built admin UI, PostgreSQL with `pg_trgm`, and matching `pg_dump`/`pg_restore` executables on PATH. On this Mac, the PostgreSQL tools are under `/Applications/Postgres.app/Contents/Versions/latest/bin`. Use a trusted local AIS2 database or isolated restore. The documented invocation uses a local Unix socket. Remote TLS/authentication and source/destination locale settings need separate deployment verification.

```sh
npm run build --prefix AliceIdentityService.Admin
umask 077
mkdir -p .dev/migration-review
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_MIGRATION_SOURCE_CONNECTION='Host=/tmp;Database=ais2;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_MIGRATION_REPORT_DIRECTORY="$PWD/.dev/migration-review" \
dotnet test --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~MigrationTests
```

Adjust local database/user names. Supply credentials privately through environment configuration if needed, never in a checked-in command or report. The source identity needs read/dump access; the test administrator needs create/drop database permission and ownership of its test objects. The source connection is opened in a read-only transaction, and pg_dump also receives read-only session options. No application host is pointed at the supplied source.

The tests export a repeatable-read snapshot and pass it to pg_dump, so counts/digests and table data come from the same snapshot. PostgreSQL sequences are not snapshot-isolated: keep source writers, including AIS2's background token pruning, quiescent for an exact baseline. The test rereads sequence state during capture and checks a fresh source fingerprint at the end; concurrent writes fail verification instead of being reported as data loss. See PostgreSQL's [snapshot functions](https://www.postgresql.org/docs/17/functions-admin.html#FUNCTIONS-SNAPSHOT-SYNCHRONIZATION) and [pg_dump snapshot option](https://www.postgresql.org/docs/17/app-pgdump.html).

Only new UUID-named `ais3_test_*` databases are restore targets. The harness refuses a populated target and never uses `pg_restore --clean` or `--create`. Source dumps live in private temporary directories and are deleted in cleanup, along with test databases. TestServer hosts are in process; no verification web listener is started. If a test process is forcibly terminated, inspect and remove only that run's temporary directory and databases; do not delete unrelated `ais3_test_*` resources from another run.

Each run writes uniquely named `ais2-*.json` and `synthetic-*.json` reports under the ignored directory when report output is configured. Check `passed` and `completedChecks`; a failed/incomplete report is not acceptance evidence. Reports include the dump digest, baseline table counts/digests, sequence state, catalog digest and aggregate review counts. Keep them private; do not commit dumps, reports, client settings or account data. The committed Step 14 review records only a non-sensitive summary. No report is produced for the source test if its opt-in variables are missing: xUnit marks it skipped.

The synthetic test persists legacy-compatible Identity V3 hashes, administrators, unconfirmed/locked/two-factor accounts, public S256 and confidential clients, a custom claim/resource scope and old protocol rows, then dumps/restores them before exercising AIS3. It checks direct/admin sign-in, account denial, code exchange, signed token claims, userinfo, refresh and logout. It also restores the original archive after disposable candidate writes and checks that the source stayed unchanged. These fixtures are not credentials or tokens from deployed AIS2 clients. A separate test checks valid code/refresh tokens with AIS2's missing-`auth_time` claim contract and matching cryptographic keys: AIS3 rejects both with `invalid_grant`.

## Session and key policy for review

The reviewed Step 14 policy for Step 15 is a planned reauthentication boundary. Preserve accounts, passwords, client IDs/secrets, scopes and all stored protocol records, but do not promise AIS2 browser sessions, account links or old grants will continue. No bulk deletion, revocation SQL or password reset is performed in this step.

| State | Cutover behavior to review |
| --- | --- |
| AIS2 browser cookie | AIS3 uses `__Host-AIS3.Session` and its own Data Protection application name. Require a new login; do not import AIS2 cookies/key-ring purposes. |
| Pending confirmation/reset links | Request a new AIS3 link after cutover. AIS3 uses its own token purposes/key ring and POST confirmation. The operational notice must cover old emailed URLs. |
| Authorization codes and refresh tokens | Clients must handle `invalid_grant` by restarting authorization. AIS3 requires current stamp/account state and `auth_time`; AIS2's principal builder does not add `auth_time`. Reusing certificates alone cannot make these grants compatible. |
| Access/ID JWTs and client-local sessions | Independently validating APIs or clients may accept old signed JWTs until expiry, especially with cached signing keys. Reauthentication is not instantaneous global revocation. Inventory actual lifetimes/caches and client-local sessions; agree on any expiry/drain window before cutover. |
| Newly issued AIS3 state | Keep the entire AIS3 certificate folder and the same environment across releases so new cookies, links and grants survive restart. |

For this policy, provision a **new AIS3-owned** `Application:CertificateFolder` and let AIS3 generate its two passwordless certificates and `keys` subfolder. Preserve AIS2's original certificate folder and configuration for rollback; never point the two applications at the same writable certificate directory. There is no KeyRingPath setting. Reusing AIS2 certificates is technically supported by hosting, but would be a separately reviewed continuity choice, not evidence of session compatibility. Keeping the existing public issuer at production cutover avoids reconfiguring client authorities; staging uses its own issuer and synthetic clients. Confirm discovery/JWKS refresh behavior with each actual client/API before changing public keys.

This policy was reviewed with Step 14; it has not been applied to a deployed service by this work. TrustedProxies still defaults to `127.0.0.1` and `::1`. AIS3 still only publishes mail to RabbitMQ; delivery and any handling of previously queued AIS2 messages belong to AliceMailService.

## Manual migrated-account/client review

1. Keep an isolated AIS2 restore for browser review using the commands in [database.md](database.md). Apply PopulateSchema.sql, run the read-only restored-database tests, then configure AIS3 Staging with that copy and separate certificates/RabbitMQ queue using [deployment.md](deployment.md). Use an isolated environment with no route to production consumers. Do not reset a real source account just to obtain test credentials.
2. Inspect the report's review counts. Resolve or explicitly accept unconfirmed accounts, missing passwords/stamps, duplicate normalized emails, two-factor users, unsupported client profiles, public clients without required PKCE and confidential clients without secrets. These aggregate checks are not a full client certification; inspect every registered client in the admin UI, including callbacks/logout callbacks, allowed scopes, resources, custom claims, secret handling and imported read-only settings.
3. With authorized representative credentials, test an ordinary user and the `ais_admin` account on the copy, plus unconfirmed/locked denial and account recovery. Verify the approved full/compact Razor pages and React/Base UI administration. Verify first/last/screen names, user ID, claim values and search results. Sign-in and recovery can mutate the copy, so do the fingerprint comparison first.
4. Exercise each of the four locally inventoried clients (the deployed inventory may differ). Include a public S256 client and confidential client, code/refresh/userinfo/logout, correct `sub`/`name`/email/profile/custom claims and audiences, invalid secret/redirect/PKCE rejection, and successful authorization after `invalid_grant`. Use test callbacks and client configurations on the copy; changing them there must not change production registrations.
5. Test client/API JWKS refresh and the planned old-session/old-link handling. Record outcomes per deployed client in private operational evidence. Synthetic tests and readable secret hashes cannot establish that real client credentials or downstream token expectations work.
6. Publish only to the isolated RabbitMQ queue if reviewing recovery/confirmation mail. Broker publication is AIS3's test boundary; mailbox delivery is a separate service check. Stop verification servers afterward.

## Rollback boundary and Step 15 handoff

The automatic rehearsal proves that the same archive restores twice with identical baseline data, including after writes to a disposable candidate. It does not start an AIS2 release or prove a production traffic switch. A manual rehearsal must also bring up the retained AIS2 release with a separate rollback copy and its original private configuration/keys, and smoke-test login and a representative client.

Before public AIS3 writes are admitted, rollback can discard the candidate and route back to the retained, stopped AIS2 database/release/keys. Do not let AIS2 keep writing while AIS3 is being validated; avoid split-brain account/password/claim state. Preserve a restricted backup of the database, original keys/configuration and exact AIS2 release. pg_dump does not back up cluster roles, service settings, key files or RabbitMQ messages; retain those separately.

After public AIS3 writes are admitted, restoring the pre-cutover AIS2 archive would lose registrations/password changes/revocations and could revive old security state. Stop traffic and writers, preserve the current database and logs, and review a forward fix or a verified AIS2 code rollback against the **current** data. Do not silently restore an old snapshot or attempt dual writes. Returning to AIS2's original keys may revive old protocol state too; review that explicitly. Step 14 makes no incompatible schema changes, but that alone does not certify AIS2 behavior after AIS3 security changes.

The manual Step 15 deployment record retains the approved session policy and client matrix, and records maintenance/write-freeze and JWT drain windows, Linux staging/browser checks, verified role/locale/TLS/backup restore procedures, release identifiers and rollback authority. No production migration, traffic change, certificate replacement or user communication is authorized by running Step 14 tests.
