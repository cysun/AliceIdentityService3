# Step 14 review — AIS2 migration rehearsal

Steps 1–13 are reviewed and committed; Step 13 is `c3112fa`. Step 14 is implemented and **uncommitted for manual review**. Stop before Step 15. Historical Step 13 status text has been corrected.

## Delivered behavior

The existing test project now rehearses a read-only AIS2 snapshot through pg_dump/pg_restore into new disposable databases. It compares every public table's count and aggregate SHA-256 content fingerprint, sequence values/state and the shared catalog, including historical migration metadata and legacy search objects. It applies the existing PopulateSchema.sql twice, reads users and protocol stores through AIS3, restores the same archive again and verifies the source is unchanged. Reports contain aggregate evidence without account rows, raw credential hashes, secrets or token payloads. Reports and dumps are not committed.

A separate dump/restore fixture exercises legacy-compatible Identity hashes, administrator access, account-state denial, public S256 and confidential client code exchange, signed claims, userinfo, refresh and logout. It checks a pre-write hash/stamp match and Identity's expected rehash/stamp rotation after sign-in. Failure tests detect equal-count credential changes, sequence drift and a missing index, and reject an initialized restore target. Another protocol test rejects missing-`auth_time` legacy-style grants even when cryptographic keys match. These are synthetic compatibility tests, not an assertion that deployed AIS2 credentials or old tokens were exercised.

The [migration guide](../migration.md) documents the reproducible commands, report interpretation, proposed reauthentication policy, manual client matrix and rollback boundaries. The proposal uses a new AIS3-owned certificate folder while retaining AIS2's keys/configuration for rollback. No keys are copied/replaced by this step. The exact public issuer, maintenance/drain window and deployed-client approvals remain Step 15 inputs after manual review.

The single backend, Razor account pages, React/Base UI administration, database mappings, mail publication boundary and deployment configuration are preserved. UI changes are limited to the step banner/version. No schema SQL, migration source, private settings, configuration samples, dependency versions or AIS2 files change.

## Automated testing instructions

From the repository root, with PostgreSQL tools on PATH:

```sh
npm run check --prefix AliceIdentityService.Admin
umask 077
mkdir -p .dev/migration-review
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_MIGRATION_SOURCE_CONNECTION='Host=/tmp;Database=ais2;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_MIGRATION_REPORT_DIRECTORY="$PWD/.dev/migration-review" \
dotnet test --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~MigrationTests
dotnet format --verify-no-changes --no-restore
dotnet publish AliceIdentityService --no-restore -c Release -o artifacts/step-14-review
git diff --check
```

Adjust connection strings. The source is only read; targets are created/dropped automatically. Use a quiescent local AIS2 database or copy for exact sequence/source comparisons. Never point an AIS3 application host or test administrator mutation directly at the source. The report directory is ignored; check the newest `ais2-*.json` and `synthetic-*.json` for `passed: true` and completed checks. Missing source/admin variables skip the source rehearsal, so a plain `dotnet test` is not complete migration verification.

For the full backend suite, also set `AIS3_TEST_RESTORED_CONNECTION` to a **separate** AIS2 dump restore with PopulateSchema.sql already applied, and `AIS3_TEST_RABBITMQ_HOST=localhost`, then run `dotnet test --no-restore -p:UseSharedCompilation=false` without a filter. Keep the migration source/report variables set. Use the [database restore commands](../database.md#ais2-compatibility) to create this extra copy and remove only that copy afterward. RabbitMQ checks publish to disposable queues without a consumer; SMTP is not involved.

## Manual review gate

1. Review the [session/key policy](../migration.md#session-and-key-policy-for-review). Approve planned reauthentication or explicitly request another continuity design before cutover work. Confirm handling of old email links, client `invalid_grant` recovery, JWKS caches, outstanding JWT lifetimes and client-local sessions.
2. Use an isolated restored database and the existing staging deployment configuration. Inspect all accounts/claims/scopes/clients in the approved UI. With authorized credentials, review ordinary/admin sign-in, unconfirmed/locked denial, profile/search, recovery and confirmation. Do not mutate the source to obtain test passwords.
3. Complete the [migrated client checklist](../migration.md#manual-migrated-accountclient-review) for every deployed client. Verify actual secrets, callback/logout addresses, scopes/resources/claims and token consumers, including public S256 and confidential clients. Record private outcomes; do not paste passwords, secrets or tokens into this review.
4. Rehearse the retained AIS2 release against a separate rollback copy with its original keys/configuration. The automatic second restore verifies data recovery only. Approve a rollback boundary that does not discard registrations/password changes/revocations after public AIS3 writes.
5. Retain the Step 13 Linux Nginx/systemd, TLS, service-permission and browser checks. Review/report files are not deployment approval. Stop verification servers, leave Step 14 uncommitted, and approve it before Step 15.

## Verification record

Local rehearsal on November 15, 2026 (America/Los_Angeles): all four focused migration tests passed. The AIS2 source remained unchanged. All 12 public tables matched after restore, with these counts:

| Table | Rows |
| --- | ---: |
| AspNetUsers | 217 |
| AspNetUserClaims | 202 |
| AspNetRoles / AspNetRoleClaims / AspNetUserLogins / AspNetUserRoles / AspNetUserTokens | 0 each |
| OpenIddictApplications | 4 |
| OpenIddictScopes | 7 |
| OpenIddictAuthorizations | 233 |
| OpenIddictTokens | 226 |
| __EFMigrationsHistory | 1 |

Both identity sequences and the shared schema matched. Aggregate review counts found 9 unconfirmed users, one administrator, no duplicate normalized-email groups, no two-factor users, no missing password hashes/security stamps, no clients outside the checked code/implicit-consent profile, no public clients lacking a PKCE requirement and no confidential clients missing a stored secret. There were 233 valid authorization rows and 151 token rows marked valid; these counts do not imply that tokens are unexpired or usable after cutover. No data repairs were applied.

The full backend run exercised **153 tests with none skipped**: 152 passed and one existing session test failed because it still asserted Step 13. That assertion now checks ApplicationInfo.Current; its focused rerun passed. All four new migration tests passed in the full run. No other backend failures occurred. The frontend's **70 tests**, formatting, lint, type checking and production build passed. The final .NET formatting check, Release publish and `git diff --check` passed. An initial sandboxed formatting attempt could not open its build-host pipe; the check passed with local pipe access.

The publish artifact is `artifacts/step-14-review`; it includes the built UI and six mail templates, and excludes private settings, certificates, migrations and test tooling. The latest successful `ais2-*.json` and `synthetic-*.json` reports are in ignored `.dev/migration-review`, with file mode `0600`. Temporary dumps and databases were removed; local verification ports 7443 and 9443 have no listeners. TestServer hosts were disposed, and no external verification server was started. Existing PostgreSQL/RabbitMQ services were left running. AIS2 source files/data and private application settings were left unchanged; both SQL scripts and tracked configuration samples are unchanged.

No remote deployment, real-client credential test, mailbox delivery or public traffic switch is included. Manual review remains required before Step 15.
