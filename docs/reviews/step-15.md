# Step 15 review — Release and manual production deployment

Steps 1–14 are reviewed and committed; Step 14 is `f7c59f8`. The user explicitly requested **release preparation and instructions for manual deployment**. Step 15 changes remain uncommitted for review. Actual production migration, traffic admission and operational handoff are operator actions, not completed by this work.

## Delivered artifacts

- `tools/package-release.py` builds a clean portable framework-dependent .NET release after locked restores and frontend checks. It excludes active settings, private keys/dumps/logs, symlinks, migration source and test tooling, requires runtime/UI/mail files, and rejects platform-specific apphosts. The archive includes SQL scripts, deployment/configuration samples, supporting guides, file checksums and a manifest recording the base commit and working-tree state. It never connects to deployment servers.
- `appsettings.Production.sample.json` and `deploy/production.conf` make the existing hosting configuration explicit for Production. The override is installed alongside the existing systemd unit. CertificateFolder still contains the two generated passwordless certificates and `keys`; no KeyRingPath setting is added, and loopback proxy defaults are unchanged. Mail remains RabbitMQ publication only.
- `CutoverTests` compares the actual frozen AIS2 source and final restored target using read-only transactions. It checks table counts/content fingerprints, sequence state, shared schema, the exact valid search-index definition, a user-store read and drift, then writes a private report. It never restores or modifies a supplied database. A separate disposable-database test rejects an incorrect index with the approved name.
- The [manual cutover runbook](../cutover.md) covers release verification, host configuration, final backup/restore, data comparison, controlled smoke checks/restart, traffic admission, operating responsibilities and recovery before/after public writes. It contains placeholders for actual server/window/role values because no target was selected for direct deployment.

Application/assembly release metadata is `3.0.0-rc.1`, implementation step 15. The account/admin review banners, preview-navigation links and visible implementation-step labels have been removed for deployment; the normal administration version display remains. The single backend, Razor account pages, React/Base UI administration, AIS2 model/credential compatibility and account/OIDC behavior are unchanged. Both SQL scripts and private settings remain unchanged. The deployment cleanup removes dependencies used only by the retired Components demo; remaining dependency versions are unchanged. The new sample stays tracked; private settings/migration source and generated artifacts remain ignored.

## Verification commands

From the repository root:

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
npm run check --prefix AliceIdentityService.Admin
dotnet format --verify-no-changes --no-restore
```

For a complete backend run, supply the existing local PostgreSQL/RabbitMQ test variables plus the new final-copy variables. In this example `ais3_step15_copy` is a **separate, disposable AIS2 restore**, with PopulateSchema.sql already applied. Create it using the database guide; do not use an existing application database as a mutable test fixture.

```sh
umask 077
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_step15_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_MIGRATION_SOURCE_CONNECTION='Host=/tmp;Database=ais2;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_MIGRATION_REPORT_DIRECTORY="$PWD/.dev/migration-review" \
AIS3_CUTOVER_SOURCE_CONNECTION='Host=/tmp;Database=ais2;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_CUTOVER_TARGET_CONNECTION='Host=/tmp;Database=ais3_step15_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_CUTOVER_REPORT_DIRECTORY="$PWD/.dev/cutover-review" \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test --no-restore -p:UseSharedCompilation=false
```

Adjust connections privately. Keep both compared databases free of writers. Require no skipped integrations and inspect the newest private reports; test queues/databases created by fixtures are cleaned up, but remove the extra restore you created yourself afterward. The final-restore test can be selected alone with `--filter FullyQualifiedName~FinalRestoreMatchesFrozenAis2BeforeApplicationWrites`; it requires only the two cutover connections and report directory, and no database-creation permission. Without its variables it is skipped, not verified. The broader `~CutoverTests` filter also selects the disposable negative-index test and needs AIS3_TEST_ADMIN_CONNECTION to run that second test.

Package into a new directory:

```sh
python3 tools/package-release.py --output artifacts/releases/ais3-step15-candidate-3
cd artifacts/releases/ais3-step15-candidate-3
shasum -a 256 --check ais3.tar.gz.sha256
```

On Linux use `sha256sum --check`. Extract into a new directory and verify the internal SHA256SUMS too. Check that the manifest version, source commit/modified flag and contents match what you intend to deploy. Preparing an uncommitted candidate does not justify labeling the base commit as the complete source revision. The packager reruns frontend checks but does not replace the backend suite.

## Manual acceptance

1. Inspect the release files and manifest. Confirm the selected host has the matching ASP.NET Core 10 runtime and PostgreSQL extension/tools, with verified TLS, service permissions and role grants. Review the Production sample/override against actual settings; do not copy placeholders unchanged.
2. Follow the [cutover runbook](../cutover.md), preserving the Step 14 reauthentication policy, source backup and original keys/configuration. Record the maintenance window, actual final dump/report, release checksum and go/no-go owner.
3. With public traffic restricted, verify readiness/discovery, existing user/admin access, real clients/claims/API use, key refresh, recovery from old grants, controlled mail publication and restart continuity. Mail delivery remains the separate consumer's responsibility. Linux Nginx/systemd/public TLS and existing-client checks remain target-host work.
4. Confirm the recovery boundary: before public writes, return to the untouched AIS2 state; afterward, preserve current data and review forward repair or compatible code rollback. Do not restore an old snapshot over newer security/account changes.
5. Record public traffic admission and handoff only when you perform them. Review these repository changes uncommitted; local verification servers must be stopped. No deployment automation or ongoing monitoring task is created.

## Local verification record

Local verification on November 15, 2026 (America/Los_Angeles): **155 backend tests passed, none skipped**, including the final-copy comparison against a separately restored AIS2 database and the negative search-index check. **70 frontend tests** and formatting/lint/type checking/production build passed. All **three release-packaging boundary tests** passed. The final .NET formatting check and portable Release publish passed. No dependency versions or SQL scripts changed.

The published candidate ran locally in **Production** with a fresh disposable database, synthetic administrator and temporary certificates. Readiness, anonymous admin denial, login, admin session/overview/assets, security headers and discovery/JWKS passed. A process restart preserved the browser session, signing keys and byte-for-byte certificate/key-ring contents. Requests used a temporary loopback HTTP listener with trusted forwarded HTTPS headers, so this does not establish Linux service permissions, Nginx/public TLS or real-client behavior. No mail was published by this smoke test. The server was stopped and its database/keys removed.

The manual release output is `artifacts/releases/ais3-step15-candidate-3/`: `ais3.tar.gz`, its SHA-256 sidecar and `release-manifest.json`. It is an uncommitted review candidate based on `f7c59f8`, not binaries from that commit alone; inspect `workingTreeModified` and the file checksums. The packager rebuilds from the current sources and performs its own frontend checks and publish-boundary checks. Follow the checksum commands above before installing.

Private migration/final-restore reports remain under ignored `.dev/migration-review` and `.dev/cutover-review`; reports contain aggregate evidence and use mode `0600`. Disposable databases, queues and verification hosts are cleaned up by their fixtures; the local smoke process and temporary files were removed. The existing PostgreSQL/RabbitMQ services are left running. AIS2 source data/files and private AIS3 settings were not changed.

Changes remain uncommitted for manual review. The actual production host configuration, final production data copy, real-client smoke checks, traffic admission and operational handoff have not been performed. These are the operator steps in the runbook.


## Deployment UI cleanup

The account-page implementation banner and its preview navigation, the administration review banner and the administration header's step number are removed. Unused banner/demo styles and account preview handlers are removed too. Account pages now fill the viewport without reserving space for the former banner. Normal branding, application version, account/OIDC behavior and administration features remain.

After this cleanup, all **70 frontend tests** and **21 existing account-route/compact-OIDC checks** passed, with no skips. Frontend formatting/lint/type checking/production build and `git diff --check` passed. Source and built UI scans found none of the removed step/review-banner text. The refreshed bundle is **`artifacts/releases/ais3-step15-candidate-2/`**; it supersedes the earlier candidate containing the review banners. Use its own manifest and checksum when deploying. Changes remain uncommitted.


## Components demo removal

The Components page, its sidebar entry and `/admin/components` React route are removed. The former URL now falls through to the existing Page not found view. Demo-only styles, its two page tests, and the unused `react-hook-form`, `@hookform/resolvers` and `zod` dependencies are removed; remaining dependency versions are unchanged.

The current deployment bundle is **`artifacts/releases/ais3-step15-candidate-3/`**, incorporating both UI cleanups. This supersedes candidate 2. All **68 remaining frontend tests** passed, along with formatting, lint, type checking and the production build. Source and built-asset checks found no Components demo route, label or chunk. Changes remain uncommitted.

For manual UI review, sign in as an administrator and check the desktop sidebar and mobile menu: only Overview, Users, Clients and Scopes should appear. Visit `/admin/components` directly and confirm the existing Page not found view appears. Verify that normal administration navigation still works and the account/admin pages have no implementation-step or review banners.
