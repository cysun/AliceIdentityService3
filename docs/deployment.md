# Linux staging deployment

This guide targets one AIS3 ASP.NET Core process behind Nginx and systemd. React is built into the same release; Razor account pages and every `/admin/` asset remain served by AIS3. Staging uses its own hostname, database, RabbitMQ queue and keys. Step 13 hosting is approved and committed as `c3112fa`. The [Step 14 migration rehearsal](migration.md) is ready for review; production cutover remains gated by Step 15.

## Configuration and keys

Use `AliceIdentityService/appsettings.Staging.sample.json` as the starting point for `/etc/ais3/appsettings.json`. Do not copy a developer's private settings into a release. Keep this private configuration file read-only to the `ais3` service account. The certificate folder, including its `keys` subfolder for Data Protection, must be persistent, service-owned storage. Environment variables can override settings using `__` separators. Restart after configuration changes.

- Set `OpenIddict:Issuer` and `Email:AppUrl` to the same public HTTPS origin, with a trailing slash and no path prefix. Set `AllowedHosts` to that one hostname, without a scheme or port.
- Omit `Hosting:TrustedProxies` when Nginx runs on the same server: AIS3 defaults to `127.0.0.1` and `::1`. If Nginx connects from another server, configure its connecting IP, for example `"Hosting": { "TrustedProxies": ["10.0.0.10"] }`. An explicit list replaces the loopback defaults and must contain individual IP addresses. One forwarded hop is processed and forwarded Host is ignored. The supplied Nginx/systemd examples use loopback; a remote proxy also needs an appropriate private listener and restricted network access. Do not enable ASP.NET Core's automatic forwarded-headers environment switch; AIS3 configures trust itself.
- Set the staging PostgreSQL connection and grant the runtime role normal access to the existing Identity/OpenIddict tables and sequences. Use a separate owner/deployment role for applying the SQL scripts. Disable `Include Error Detail` and `Log Parameters`; AIS3 rejects these options when enabled. Use certificate-verified TLS for remote PostgreSQL/RabbitMQ connections, or the explicitly trusted local/private transport.
- Configure a dedicated RabbitMQ user and queue for staging. AIS3 only publishes; a consumer is optional for publication checks. Actual delivery and retries remain AliceMailService responsibilities.
- Set `Application:CertificateFolder` to an absolute persistent directory outside the release, such as `/var/lib/ais3/certificates`. In Staging/Production, AIS3 loads `signing-certificate.pfx` and `encryption-certificate.pfx` from this folder and generates whichever file is missing. No certificate-generation command or PFX password is needed. Development/Testing keep their existing certificate behavior.
- AIS3 reuses the encryption certificate to encrypt the Data Protection key ring. Keeping signing and encryption separate follows [OpenIddict's production recommendation](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials). Nginx still uses its own public HTTPS certificate. These application certificates are self-signed; OpenIddict publishes the signing public key through discovery.
- AIS3 automatically stores the Data Protection key ring in `keys` under `Application:CertificateFolder`, such as `/var/lib/ais3/certificates/keys`. No separate key-ring path setting is needed. Its application name is fixed to `AliceIdentityService3.Staging` (or `.Production`), independent of the release path. Retain the entire certificate folder across deployments: the encryption certificate protects the stored keys, while those keys protect cookies and account links. Keep each environment's keys separate. Losing or replacing keys invalidates protected state; changing the environment also changes the application name.

The staging sample uses:

```json
{
  "Application": {
    "CertificateFolder": "/var/lib/ais3/certificates"
  }
}
```

On first start, AIS3 creates the certificate folder and its `keys` subfolder with mode `0700` and passwordless PFX files with mode `0600` on Linux. New certificates use RSA 3072 with signing/encryption key usages and a validity end date of December 31, 9999: X.509 requires an end date, but these keys need no routine renewal. Their private keys are protected by filesystem permissions and restricted backups. Files are published atomically; concurrent first starts reuse the winning files.

Existing passwordless RSA certificates with private keys of at least 2048 bits are reused unchanged, including AIS2-style files with these names. They must still be currently valid and have the appropriate key usage if specified. Imported certificates retain their original expiry dates; AIS2's generator used ten years. An invalid, expired, password-protected or unreadable existing file fails startup and is never automatically replaced.

The host rejects missing/invalid deployment settings and unsuitable active certificates. Before serving traffic it decrypts existing unrevoked Data Protection keys and exercises protection/unprotection. The database readiness probe checks the existing user table; it does not apply SQL. It does not test RabbitMQ or mailbox delivery.

For a fresh staging database, explicitly apply CreateSchema.sql followed by PopulateSchema.sql. For an isolated AIS2 restore, apply only PopulateSchema.sql. Never run CreateSchema.sql on an AIS2 restore. See [database setup](database.md). No schema or SQL changes are introduced by Step 13, and startup never runs migrations or bootstrap. Create a staging administrator explicitly with the existing `bootstrap` command if needed; supply the password through its hidden prompt, not an argument or shell history.

## Build and install

From the repository root:

```sh
npm ci --prefix AliceIdentityService.Admin
npm run check --prefix AliceIdentityService.Admin
dotnet restore --locked-mode
dotnet test
dotnet publish AliceIdentityService --no-restore -c Release -o artifacts/staging
```

The plain test command skips opt-in integrations; the [Step 13 checklist](reviews/step-13.md) gives the complete test environment. Publish as a framework-dependent deployment and install the matching ASP.NET Core 10 runtime on Linux. The published directory must contain `wwwroot/admin/index.html`, account assets and all six EmailTemplates files; it must contain no private appsettings.json, PFX/private key, migration source or review client.

Provision a dedicated non-login `ais3` user/group. Use this layout:

```text
/opt/ais3/releases/<release-id>/   Published files, owned by the deployment administrator
/opt/ais3/current                 Symlink to the selected release
/etc/ais3/appsettings.json        Private settings, root:ais3, mode 0640
/var/lib/ais3/certificates/        Persistent certificates, ais3:ais3, directory 0700/files 0600
/var/lib/ais3/certificates/keys/   Persistent key ring, ais3:ais3, directory mode 0700
```

Place a symlink named `appsettings.json` in each installed release pointing to `/etc/ais3/appsettings.json`. The release directory itself remains read-only to the service. This keeps configuration and keys independent of code rollback. Back up the database, private configuration, key ring and certificates together into restricted backup storage; do not put them into Git or a public artifact store.

Install [ais3.service](../deploy/ais3.service) into `/etc/systemd/system/`, adjusting `/usr/bin/dotnet` if the distribution uses another path. Its `StateDirectory` creates the writable `/var/lib/ais3` directory with mode `0700`; AIS3 creates `certificates/` and its `keys/` subfolder there. Keep this certificate-folder path for the sample unit, or explicitly grant writable service storage when choosing another location. It runs Staging on loopback port 5000 with a read-only system filesystem, private temporary directory and no extra capabilities. Install [nginx.conf](../deploy/nginx.conf) in Nginx's `http` context after replacing its hostname and HTTPS certificate paths. Retain the site's existing default-host rejection configuration. Proxy every path through AIS3; never add an Nginx static-file shortcut for `/admin/`.

On the staging host, validate before starting:

```sh
sudo systemd-analyze verify /etc/systemd/system/ais3.service
sudo nginx -t
sudo systemctl daemon-reload
sudo systemctl enable --now ais3
sudo systemctl reload nginx
curl --fail --header 'Host: ais3-stage.example.org' http://127.0.0.1:5000/health/ready
curl --fail https://ais3-stage.example.org/.well-known/openid-configuration
```

The public proxy returns 404 for `/health`; local probes use the exact configured Host header. HTTP account requests redirect to the public HTTPS port. Verify direct login, OIDC/PKCE/refresh, account links, administration and RabbitMQ publication with synthetic accounts before admitting staging reviewers. The service is intentionally single-instance: account email limits are in memory and reset on restart; multi-instance rate limiting and shared key storage require a separate deployment design.

## Logging and browser policy

The sample writes application logs to systemd's journal. Request logs use route templates, method, status, duration and a generated trace ID; they omit raw paths, query strings, bodies, cookies and authorization headers. Unhandled request failures return a generic 500 and log only exception type and trace ID. Existing administrative operation logs record actor/target identifiers, and mail logs retain the Step 12 publication behavior. Restrict journal access and configure retention on the host. Do not enable request-body logging, EF sensitive-data logging or verbose OpenIddict/framework logs on a shared identity host. Diagnostic exports must be reviewed for account data.

Nginx's sample access format omits URLs and headers. Its error log threshold is `crit` because ordinary proxy errors can include full request URIs; use AIS3 status/trace logs and local health probes for routine diagnosis. Review any server-wide/default-host logging rules too. The proxy must overwrite forwarded headers rather than append user-supplied values.

Deployed hosts add HSTS (180 days, no subdomain/preload opt-in), frame denial, `nosniff`, no-referrer and a restricted permissions policy. Account/admin documents use a same-origin Content Security Policy with no inline scripts; inline styles remain allowed for Base UI positioning. `/connect` responses retain framing/base/object restrictions while allowing OpenIddict's generated cross-origin `form_post` form/script. This preserves existing OIDC response modes. The approved UI layout and account flows are unchanged.

## Restart and rollback

If you ran an earlier Step 13 build with a separate `DataProtection:KeyRingPath`, stop AIS3 and back up its certificate folder and old key directory. Move the entire old key directory (for example `/var/lib/ais3/keys`) to `<CertificateFolder>/keys`, preserving all XML files and service ownership, then remove the obsolete setting before starting this build. If the destination already contains keys, reconcile the directories before proceeding; do not overwrite or discard key files. Keep the same certificates and environment so existing cookies and account links remain readable. Fresh installations need no migration.

1. Before replacing a release, save the current release identifier and back up state/configuration. Install the candidate into a new directory and add its private-settings symlink. Do not edit the active release in place.
2. Stop `ais3`, atomically replace `/opt/ais3/current` with a symlink to the candidate, then start it. This single-instance procedure has a short maintenance window. Run local readiness and the public smoke checks. Confirm an existing AIS3 browser session, outstanding reset/confirmation link and refresh token still work across the restart.
3. If checks fail, stop `ais3`, switch the symlink back to the previous **staging-capable** release and restart. Keep the same database, environment, configuration and keys. Step 12's development-only host is not a staging rollback target. Rehearse using two Step 13 staging releases before the first real update. Step 13 makes no schema changes, so release rollback does not need database rollback. Do not restore an old database merely to revert code: it would discard newer account/security changes and could revive revoked state.

No certificate changes are needed during routine deployments. Preserve both named PFX files and all key-ring XML files, including in backups. Generating a missing file cannot recover its former private key: replacing signing keys affects outstanding signed tokens, and replacing encryption keys affects outstanding encrypted tokens and the stored Data Protection keys. If the original encryption certificate is missing, AIS3 generates the requested missing file but then fails its existing-key-ring decryption check; restore the original certificate from backup. Never delete the key ring to bypass that check. Credential replacement after loss or compromise requires a separate recovery plan and may invalidate sessions/tokens.

These instructions do not establish AIS2 token/cookie continuity. The [Step 14 migration guide](migration.md) proposes a planned reauthentication boundary with a fresh AIS3-owned certificate folder, requires real-client/JWKS review, and distinguishes rollback before and after public writes. Keep AIS2 keys/configuration intact and separate. Approval of Step 14 and the Step 15 cutover gate are required before production use.

References: [ASP.NET Core Nginx/systemd hosting](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0), [trusted proxy configuration](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0), and [OpenIddict certificate selection](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials).
