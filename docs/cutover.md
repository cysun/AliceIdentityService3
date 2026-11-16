# Manual production cutover and operations

Step 14 is reviewed and committed as `f7c59f8`. Step 15 prepares a release and these instructions for **manual deployment**, as requested. No production server, database, proxy, DNS record or certificate has been changed. Complete the deployment record below with actual values before executing server commands. The examples target the single Linux Nginx/systemd host from the staging guide.

## Release and deployment record

Record these values privately with the operator's results; credentials belong in private configuration/password storage, not this document or Git.

| Item | Record before cutover |
| --- | --- |
| Release | Archive SHA-256, release-manifest.json, version, source commit and whether the working tree was modified |
| Host and issuer | Server, existing public HTTPS issuer/hostname, TLS certificate paths and Nginx site file |
| Services | Exact AIS2 unit/process and background jobs to stop; AIS3 service account, unit, listener and release directory |
| Data | Source and separately named target databases/servers; encoding, collation/provider; owner and runtime roles; private connection configuration |
| Persistent storage | AIS2 original configuration/certificates/key ring backup; new AIS3 certificate folder, private settings path and backup location |
| Mail | Existing RabbitMQ consumer contract, queue, credentials/transport, test recipient and mail-service owner |
| Timing | Maintenance start/end, operator, rollback decision owner and deadline; expected JWT/key-cache/client-session lifetime behavior |
| Acceptance | Step 14 approval, existing-client outcomes, Linux staging checks and backup/restore evidence |

Use the reviewed reauthentication policy: keep user passwords, client IDs/secrets and database records; require fresh AIS3 login and replacement account links. Clients must recover from `invalid_grant`. Preserve the public issuer unless a client authority change has been separately coordinated. Provision a fresh AIS3-owned certificate folder and retain AIS2's original files for rollback. Existing client-local sessions/JWTs may remain valid until expiry; the maintenance window does not itself revoke them. The Step 14 approval advances this work; it is not evidence that a production traffic switch has occurred.

## Build and verify the bundle

From the source checkout, run the backend integration checks in the Step 15 review, then:

```sh
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/package-release.py --output artifacts/releases/ais3-step15-candidate-3
```

Use a new output directory for each build. The packager runs locked npm/.NET restores, frontend checks and a clean Release publish. It builds without a platform-specific apphost: the server runs `dotnet AliceIdentityService.dll`. Install the matching ASP.NET Core **10** runtime for the Linux machine's architecture; Node, Python, the .NET SDK and the review client are not runtime dependencies. The archive is framework-dependent, not a self-contained Linux executable.

Output contains `ais3.tar.gz`, `ais3.tar.gz.sha256` and an inspectable `release-manifest.json`. The manifest records the base commit, modified-working-tree flag and SHA-256 for each payload file. A modified tree is expected for the uncommitted review candidate; the commit alone does not identify its binaries. Rebuild after source changes/commit if you want a clean source identifier, and deploy the exact verified archive. The packaging command does not claim backend tests or production smoke checks passed.

On Linux, in the directory containing the transferred archive and checksum:

```sh
sha256sum --check ais3.tar.gz.sha256
tar -xzf ais3.tar.gz
cd ais3
sha256sum --check SHA256SUMS
```

On macOS, `shasum -a 256 --check` can perform the same checks. Extract into a new operator-controlled directory. Inspect the manifest and retain the checksums with the deployment record.

Bundle layout:

```text
ais3/
  app/                  Runtime binaries, Razor/account assets, built admin UI and email templates
  configuration/        Production sample only; never copied into app/ as an active sample
  database/             CreateSchema.sql and PopulateSchema.sql
  deploy/               systemd unit, Production override and Nginx example
  docs/                 This runbook and supporting review/deployment guides
  release-manifest.json
  SHA256SUMS
```

This runbook uses **bundle paths**. Supporting guides retain **repository paths** for development/testing. Private settings, certificates, Data Protection keys, database dumps, test reports and test tooling are excluded from the bundle. Keep checksums outside the installed runtime; active configuration is an external symlink and is not a release-manifest input.

## Prepare the host without changing traffic

1. Confirm the Linux staging checks and actual client outcomes accepted in Step 14. Record any explicitly accepted limitations. Verify PostgreSQL tools/extensions, runtime version, firewall, public TLS, the existing proxy layout and backup access on the target host. Keep the current AIS2 release/configuration/proxy configuration available.
2. Provision the dedicated non-login `ais3` account if it does not exist. The runtime account needs ordinary table read/write and Identity-sequence access; only the owner/deployment role needs schema/extension permissions. Restore roles/grants deliberately: `--no-owner --no-privileges` does not reproduce source ACLs. Check locale/provider compatibility before creating the target database. Do not grant the application superuser or create-database privileges.
3. Create a new `/opt/ais3/releases/<release-id>` directory. Copy **only `app/` contents** there, owned by the deployment administrator and readable by `ais3`. Refuse to overwrite an existing release directory. Keep `/opt/ais3/current` unchanged until the maintenance window.
4. Prepare `/etc/ais3/appsettings.json`, owned by `root:ais3`, mode `0640`, from `configuration/appsettings.Production.sample.json`. Never overwrite an existing private configuration with the sample. Set the target database, exact public issuer/email origin/AllowedHosts, approved sender and existing mail queue/transport/credentials. Replace every `CHANGE_ME` and example hostname/address. The sample queue name is an example, not a deployment discovery result.
5. Set `Application:CertificateFolder` to fresh AIS3-owned persistent storage, normally `/var/lib/ais3/certificates`. AIS3 creates the two passwordless certificates and the `keys` subfolder. No KeyRingPath setting is used. Never share AIS2's or staging's writable certificate folder. Keep this path and the Production environment stable afterward. Omit TrustedProxies for local Nginx: `127.0.0.1` and `::1` are already trusted. Explicit remote proxies replace those defaults.
6. Place an `appsettings.json` symlink in the **new** runtime directory pointing to `/etc/ais3/appsettings.json`. Install `deploy/ais3.service` as `/etc/systemd/system/ais3.service` and `deploy/production.conf` as `/etc/systemd/system/ais3.service.d/production.conf`. The override changes the environment and description to Production. Review the merged unit, including any existing overrides; do not start it yet. If staging shares this host, use separate unit/storage/listener paths and review them first.
7. Adapt `deploy/nginx.conf` for the recorded production hostname and existing public TLS certificate. Its default hostname is a staging example and must be replaced everywhere. Proxy every path, including `/admin/` assets, through AIS3. Preserve safe logging and local-only health checks. Prepare the change separately without replacing the active AIS2 upstream yet.

Validate the unit and candidate Nginx configuration using the host's normal configuration validation workflow. The existing unit uses `/usr/bin/dotnet`, loopback port 5000 and `StateDirectory=ais3`; adjust them if the actual host differs. Do not switch the environment from Staging to Production while expecting staging cookies/account links to survive: the Data Protection application name changes.

## Maintenance window: final data copy

Before proceeding, confirm the recorded rollback owner/window and client/key-cache plan. Put public ingress into maintenance using the existing proxy/load-balancer mechanism, with access retained only for the operator's smoke tests. Stop AIS2 **and every background/direct database writer**, including its token cleanup worker. Keep both application hosts stopped during the copy. Verify there are no remaining writers; stopping browser traffic alone is insufficient.

Take the final database backup and restricted backups of AIS2's exact release, private configuration, certificates/key ring and proxy/service settings. PostgreSQL dumps do not include those files, cluster roles or RabbitMQ messages. Record the database backup digest and time. Retain the original database untouched for the pre-public-write rollback path.

The following commands use operator-configured PostgreSQL service entries; store passwords in protected password files, not command arguments. `AIS2_PG_SERVICE` must point to the frozen source; `AIS3_PG_SERVICE` must point to a newly created, **empty, separately named** target with matching encoding/locale/provider. Use the database owner/deployment identity for restore and PopulateSchema. Inspect both connections and target emptiness before running restore. This procedure never uses `--clean` or restores over the AIS2 database.

```sh
# Set these to the recorded absolute paths/service names before running commands.
: "${AIS2_PG_SERVICE:?Set the frozen source service name}"
: "${AIS3_PG_SERVICE:?Set the empty target service name}"
: "${AIS3_BACKUP_DIR:?Set a new restricted backup directory}"
: "${AIS3_BUNDLE:?Set the extracted ais3 bundle directory}"
umask 077
mkdir "$AIS3_BACKUP_DIR"
PGOPTIONS='-c default_transaction_read_only=on' pg_dump \
  --format=custom --no-owner --no-privileges \
  --file="$AIS3_BACKUP_DIR/ais2-final.dump" "service=$AIS2_PG_SERVICE"
sha256sum "$AIS3_BACKUP_DIR/ais2-final.dump"
pg_restore --exit-on-error --single-transaction --no-owner --no-privileges \
  --dbname="service=$AIS3_PG_SERVICE" "$AIS3_BACKUP_DIR/ais2-final.dump"
psql -X "service=$AIS3_PG_SERVICE" -v ON_ERROR_STOP=1 \
  -f "$AIS3_BUNDLE/database/PopulateSchema.sql"
```

**Do not run CreateSchema.sql or bootstrap on this restore.** Apply the runtime-role grants and verify access separately. Confirm the approved trigram expression index is present/valid; PopulateSchema is rerunnable but does not repair a different existing object with the same name.

Before any application sign-in, validate the actual final target from the source checkout on a trusted machine with database access:

```sh
# Set both Npgsql connection strings privately. These are distinct from libpq service names.
: "${AIS3_CUTOVER_SOURCE_CONNECTION:?Set the frozen AIS2 source connection}"
: "${AIS3_CUTOVER_TARGET_CONNECTION:?Set the restored AIS3 target connection}"
export AIS3_CUTOVER_SOURCE_CONNECTION AIS3_CUTOVER_TARGET_CONNECTION
export AIS3_CUTOVER_REPORT_DIRECTORY="$PWD/.dev/cutover-review"
dotnet test --no-restore -p:UseSharedCompilation=false --filter FullyQualifiedName~FinalRestoreMatchesFrozenAis2BeforeApplicationWrites
```

Use read-only credentials/session options for both. The test itself opens read-only transactions, compares all public table counts/content digests, sequences and shared schema, verifies the target search index/store, and rereads both databases for drift. It does not migrate, repair, start a server, generate keys or send mail. **Require one passed test, zero skipped, and a new `cutover-*.json` report with `passed: true`.** Reports use mode `0600`; retain them privately with the final dump checksum. Do not mistake a skipped test or an old report for acceptance. Repeat the final dump into another empty target if writers changed the source; never merge competing copies automatically.

## Start the candidate and perform controlled smoke checks

Keep public maintenance in effect. After the database comparison passes, atomically set `/opt/ais3/current` to the new runtime directory, then start AIS3. On GNU/Linux, for a recorded existing release directory:

```sh
: "${AIS3_RELEASE:?Set the new installed runtime directory}"
test -f "$AIS3_RELEASE/AliceIdentityService.dll"
sudo ln -s "$AIS3_RELEASE" /opt/ais3/current.next
sudo mv -Tf /opt/ais3/current.next /opt/ais3/current
sudo systemctl daemon-reload
sudo systemctl start ais3
sudo systemctl status ais3 --no-pager
```

If `current.next` already exists, inspect it first; do not overwrite an unexplained pending switch. `current` must be a symlink, not an installed directory. Validate/load the prepared proxy configuration while preserving the maintenance restriction. Confirm the merged unit is running in **Production**, not Staging.

Use the real recorded hostname, without a scheme/port, for local probes:

```sh
: "${AIS3_HOST:?Set the public issuer hostname}"
curl --fail --header "Host: $AIS3_HOST" http://127.0.0.1:5000/health/ready
curl --fail --header "Host: $AIS3_HOST" --header 'X-Forwarded-Proto: https' \
  http://127.0.0.1:5000/.well-known/openid-configuration
```

Check that discovery's issuer and all endpoint/JWKS URLs use the existing public HTTPS origin, not localhost or a staging name. Then, through the actual HTTPS proxy using operator access, complete these checks and record results:

- Direct login with an existing authorized user; administrator login and current release version; anonymous and ordinary-user denial of administration. Verify the approved full/compact account pages and admin assets load without browser console/CSP failures.
- At least one public S256 and one confidential client where applicable, then the agreed smoke action in each real client: callback, current claims/audience, API use, refresh, userinfo if used, and logout. Verify JWKS refresh and recovery from rejected AIS2 refresh tokens.
- Confirmation/recovery with a designated controlled test account and intended recipient, if part of the cutover checklist. AIS3 confirms RabbitMQ publication only; the mail-service owner checks delivery. Do not submit arbitrary real-user reset requests. Keep old queued AIS2-link handling consistent with the approved policy.
- Existing users/claims/scopes/clients and search look correct. Controlled smoke writes belong only to designated test accounts. Do not change real passwords/permissions as a smoke test before the rollback decision.
- Restart AIS3 once while still restricted. Confirm new AIS3 browser state/refresh tokens survive and the two PFX files and `keys` directory are reused. Verify service ownership, file permissions and backup access on Linux. Never delete keys to make a failed restart work.
- Check logs for startup failures, database permission errors, repeated authentication failures, RabbitMQ publication failures and proxy redirect loops. Do not enable body/token/query-string logging to diagnose failures.

Readiness only proves a user-table query succeeds. It does not certify all database grants, OIDC clients, RabbitMQ or mailbox delivery. A successful HTTP discovery probe is not a replacement for authenticated client smoke tests.

## Admit public traffic and hand off

The operator records a go/no-go decision after the preceding checks. On go, keep AIS2 and its background writers stopped, lift maintenance, enable AIS3 for boot and prevent automatic restart of the old AIS2 writer. Record the exact time public writes were admitted; that changes the rollback boundary. Observe real login/client activity, API errors, readiness, mail publication and consumer delivery according to the recorded window. If checks fail, follow the recovery path below.

Hand off the release/checksums, final dump/report, private configuration locations, complete certificate-folder backup, source/target identities, client results and any accepted limitations. Name owners for service/database/key backups, access control/log retention, RabbitMQ publication/consumer delivery and incident response. Keep backup and log retention durations in the host's operating policy. Retain the old release/database/keys until the agreed rollback retention period expires; this step does not delete them.

## Recovery

**Before public writes:** keep maintenance, stop AIS3, restore the recorded AIS2 proxy/service/configuration selection and start only AIS2 against its untouched source database with its original keys. Verify existing-user/client sign-in, then lift maintenance. Preserve the failed candidate/database/logs for diagnosis; do not restore a dump over either database. Discarding designated candidate smoke-test changes is an explicit part of this pre-public-write rollback.

**After public writes:** put traffic back into maintenance and stop writers. Preserve/back up the current AIS3 database and complete certificate folder before recovery work. Restoring the old AIS2 snapshot could erase registrations/password changes/revocations and revive old grants. Prefer a forward fix or a separately verified application rollback against current data; the data compatibility of the schema alone does not establish security/session compatibility. Restoration after actual data loss needs a separately assessed recovery point and loss decision. Do not silently switch to the stale AIS2 database or run both writers.

For subsequent AIS3-only release rollback, retain the current database, Production environment, private configuration and whole certificate folder while switching code to a compatible prior AIS3 release. The staging guide's restart procedure applies. A missing/corrupt encryption certificate requires restoring the original from backup; generating another one cannot decrypt the old key ring.

Manual deployment is complete only when the operator records the actual migration, smoke tests, traffic admission and handoff. The prepared Step 15 candidate and local verification do not record those events as completed.
