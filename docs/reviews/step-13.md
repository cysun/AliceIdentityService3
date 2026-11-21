# Step 13 review — Staging and hardening

Steps 1–13 are reviewed and committed. Step 13 is committed as `c3112fa`; earlier uncommitted-status wording is historical. Step 14 is authorized and documented in the [Step 14 review](step-14.md).

## Delivered behavior

Staging and Production hosts now require explicit HTTPS origins, a single allowed hostname, persistent encrypted Data Protection storage and `Application:CertificateFolder`. AIS3 reuses `signing-certificate.pfx` and `encryption-certificate.pfx` from that folder, generating each missing file automatically without a password. New certificates are valid through year 9999, avoiding routine renewal; existing files are never overwritten. The encryption certificate also protects the Data Protection keys in the automatically created `keys` subfolder; there is no separate key-ring path setting. Development and Testing retain their existing behavior. Missing or unsafe deployment configuration fails startup; a bad existing key ring cannot silently fall back to new sessions. Startup performs no database migration or RabbitMQ publication.

Proxy trust defaults to `127.0.0.1` and `::1`, so local Nginx needs no `Hosting:TrustedProxies` setting. An explicit list replaces these defaults for a proxy on another server. Hosting adds security headers, a generic exception response, request logging without raw URLs or request data, and stable keys across release paths. Preserve the certificate folder and key ring during deployments. OpenIddict's `form_post` mode remains supported; the stricter account/admin CSP does not block it. Authentication, CSRF, administrative authorization and publication remain in their existing services.

The [deployment guide](../deployment.md), [staging settings sample](../../AliceIdentityService/appsettings.Staging.sample.json), [Nginx example](../../deploy/nginx.conf) and [systemd unit](../../deploy/ais3.service) describe one Linux host and a short maintenance-window deployment/rollback. No remote deployment, production cutover, mail consumer feature, schema/SQL change or dependency update is included. The approved UI changes only its step banner. Private settings and migration sources stay ignored; sample settings stay tracked.

## Automated checks

From the repository root, using local test services:

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
dotnet publish AliceIdentityService --no-restore -c Release -o artifacts/step-13-review
git diff --check
```

Adjust connections. The restore must be a separate AIS2 copy with PopulateSchema.sql already applied. PostgreSQL tests create/drop their own temporary databases, and RabbitMQ tests use isolated queues. No consumer is needed. Run only hosting checks with `AIS3_TEST_ADMIN_CONNECTION=... dotnet test --filter FullyQualifiedName~HostingTests`.

Hosting tests cover rejected configurations, Staging and Production startup, IPv4/IPv6 loopback proxy defaults without configuration, explicit proxy overrides that replace those defaults, rejection of forwarding headers from untrusted peers, host rejection, HTTPS/HSTS/CSP, protected admin routes, generic errors and private request logs. Certificate checks cover concurrent first-start generation, passwordless loading, year-9999 validity, Unix permissions, reuse of either existing file and refusal to replace corrupt, password-protected, expired or inappropriate certificates. A PostgreSQL flow logs in, issues codes/refresh tokens and confirmation/reset tokens, restarts from a different release directory using the automatically generated certificates, then checks the cookie, account tokens and old protocol grants. Both PFX files and the existing encrypted key-ring XML in `CertificateFolder/keys` must remain byte-for-byte unchanged. Tests also check the nested directory permissions and reject an unusable `keys` subfolder. Losing the encryption certificate must fail the existing-key-ring check without replacing its XML files.

## Manual staging review

1. Follow the deployment guide on an isolated Linux staging host. Validate the actual Nginx configuration and systemd unit there; the development machine cannot establish Linux service permissions, firewall rules or public TLS behavior. Leave `Hosting:TrustedProxies` omitted for local Nginx and verify public HTTPS login works without redirect loops.
2. Set only `Application:CertificateFolder` to `/var/lib/ais3/certificates` for certificate/key storage. With a fresh empty folder, start the service and check that the two named PFX files appear with mode `0600` in a `0700` folder owned by `ais3`, along with a `0700` `keys` subfolder containing encrypted key-ring XML. If upgrading an earlier Step 13 build, move its existing key directory as described in the deployment guide first. Restart and verify their hashes are unchanged. Using disposable copies, check startup failures with a missing folder setting, relative path, corrupt/password-protected existing file or unwritable directory. Also check mismatched mail/issuer origins and wildcard AllowedHosts. Restore each setting afterward. Verify health/readiness without exposing `/health` through the public proxy.
3. Check full and compact Razor layouts, direct login/logout, registration/confirmation, recovery/change, the React overview/user/claim/scope/client pages and the dialogs. Inspect browser console/network for CSP failures; use the production-built UI. Verify anonymous/non-admin denial, stale-session handling and CSRF protection.
4. Exercise a representative public PKCE client and confidential client, including token refresh, userinfo and logout. Confirm discovery and generated email links use the staging HTTPS origin. Publish synthetic mail to the isolated staging queue; delivery testing, if desired, belongs to the separate mail service.
5. Rehearse replacing and rolling back a staging-capable release using the guide. Preserve the database, configuration and entire certificate folder, including its `keys` subfolder. Confirm cookies, pending account links and refresh tokens survive. Do not delete or regenerate existing keys during deployment.
6. Check that a registration records the account email and ID, a confirmed mail publication names the queue, and ordinary page views and searches stay quiet. Check unhandled failures for a trace ID and exception type. Keep passwords, tokens, client secrets, query strings, cookies and message bodies out of logs. Review Nginx's server-wide logging and restrict journal/file access and retention.
7. Stop local verification servers and remove only disposable verification databases/queues/keys. This review gate is complete; retain this checklist for future staging checks.

## Verification

Before the certificate-folder follow-up, **135 backend tests passed, none skipped; 70 frontend tests passed**. Frontend formatting, lint, type checking and production build passed. .NET formatting, Release publish and `git diff --check` passed. npm and NuGet advisory checks (including transitive dependencies) reported no vulnerabilities; dependency versions were unchanged. The focused checks for automatic certificate setup are recorded below.

The earlier published Staging build was run locally over HTTPS with a synthetic administrator, a fresh disposable database and temporary certificates. Login, account/admin APIs, protected admin assets, security headers, readiness and discovery passed. A release-directory A → B → A restart/rollback rehearsal retained the signed-in session and discovery signing keys. The two directories contained the same Step 13 build: this verifies the deployment mechanics and key persistence, not rollback across a future schema or application-contract change.

With automatic certificates, the nested `keys` directory in `Application:CertificateFolder` and default loopback proxy trust, **all 27 hosting tests passed, none skipped**, including default/overridden proxy trust in Staging and Production and PostgreSQL checks for cookies, confirmation/reset tokens, authorization codes, refresh tokens and access-token validation across a release-directory change. .NET formatting, Release publish, sample JSON validation and `git diff --check` passed. The review publish output was refreshed. No external verification server was started for this follow-up; disposable test hosts, databases and keys were cleaned up by the fixtures.

The publish output contains the built admin UI and all six email templates, with no settings JSON, private certificates or migration source. The disposable databases, queues, releases, keys, cookies and dump were removed. Verification server ports 7443 and 9443 have no listeners. The source AIS2 database and private application settings were left unchanged.

Nginx/systemd are not installed on the macOS verification machine, so their Linux execution, filesystem permissions, firewall and public TLS checks remain manual. Browser automation could not attach to the temporary self-signed HTTPS page; visual/CSP behavior remains in the manual checklist. No remote service was changed. Step 13 was subsequently reviewed and committed as `c3112fa`. Step 14 now follows in its own review record.
