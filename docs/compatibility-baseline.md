# AIS2 compatibility baseline

Baseline inspected from the adjacent AIS2 source and the current local PostgreSQL `ais2` database on 2026-11-14. Steps 2–4 implement persistence, direct authentication, and initial protocol compatibility. Real-client migration and cutover remain later review gates. AIS2 remains the reference and is not modified by AIS3.

| Area                    | Existing behavior / source                                                                                                     | AIS3 acceptance requirement                                                                                                                            |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Platform                | .NET 10, Identity/EF Core, PostgreSQL, OpenIddict. `AliceIdentityService/Program.cs`                                           | Keep the platform; pin compatible package versions when each subsystem is introduced.                                                                  |
| Users                   | Identity user IDs/password hashes plus first name, last name, screen name, creation time; computed full name. `Models/User.cs` | Preserve stable IDs, hash compatibility and user-visible field semantics; validate with representative migrated users.                                 |
| Administrator           | Actual claim is **`ais_admin`**. AIS2 README contains a hyphenated spelling. `Program.cs`, user/controller and console code    | Use the actual underscore claim contract; verify policies and bootstrap behavior. Do not silently change it to a role or `ais-admin`.                  |
| Login                   | Email confirmation required; failed sign-ins count toward lockout. `Controllers/AccountController.cs`                          | Review confirmed/unconfirmed/locked states and preserve intended policy.                                                                               |
| Registration / recovery | Register, confirm email, forgot password, reset password and password changes. Account controller and views                    | Retain working recovery and confirmation flows; carry validated OIDC context when appropriate.                                                         |
| Account profile         | AIS2 profile display; new approved plan adds editing                                                                           | Preserve display and review editing as its own feature; Step 1 contains account layout samples only.                                                   |
| OIDC endpoints          | `/connect/authorize`, `/connect/token`, `/connect/userinfo`, `/connect/logout`. Program and authorization controller           | Preserve paths and supported client behavior. Verify actual userinfo behavior; endpoint registration alone is not proof of a completed implementation. |
| OAuth flow              | Authorization code and refresh tokens, `code` response; client PKCE setting. Automatic/implicit consent                        | Preserve supported flows. “Implicit consent” is not the OAuth implicit grant. Verify consent and PKCE independently.                                   |
| Clients                 | Client IDs, display names, redirect/logout URIs, secrets, scopes, permissions                                                  | Migrate and test representative confidential/public clients; do not silently widen permissions.                                                        |
| Scopes and claims       | Scope-to-claim selection and access/identity-token destinations in authorization code                                          | Record token payload contracts and test against real client expectations before replacement.                                                           |
| Database                | Existing SQL-managed schema and custom `SearchUsers` function in `Scripts/PopulateSchema.sql`                                  | Inventory schema/functions and data transformations before migrating; do not assume EF-generated schema is identical.                                  |
| Mail                    | RabbitMQ publishes messages consumed by AliceMailService                                                                       | Preserve agreed message schema, queue/routing behavior, templates, and retries in the mail step. No mail is sent by Step 1.                            |
| Console                 | Existing initialization/admin operations in `ConsoleManager`                                                                   | Provide bootstrap as a command mode of the single application.                                                                                         |
| Keys and sessions       | Signing/encryption certificates, Identity cookies, Data Protection keys, persisted OpenIddict data                             | Decide cutover behavior explicitly. Do not promise existing sessions/tokens survive without a tested key/storage migration.                            |

## Intentional improvements

Do not reproduce unsafe legacy mechanics. AIS3 must validate local return URLs or OIDC redirect context; mutation endpoints must use the appropriate HTTP methods and request protections. Existing GET-based deletes and permissive redirects are reference behavior to replace, not compatibility requirements.

## Decisions deferred to their review steps

Step 2 uses ignored appsettings.json files and explicit bootstrap command inputs. Email/provider configuration, real clients/redirect URLs, key management, migration downtime, session continuity, staging hostnames, and production deployment packaging need concrete configuration and testing at their respective steps.

## Step 2 verified persistence baseline

- A complete AIS2 dump restores directly into `ais3`, with no schema/data transformations. All 12 tables have matching row counts and content fingerprints before/after the AIS3 read checks. The source `ais2` is unchanged.
- User IDs, password hashes, security/concurrency stamps, Identity claims/logins/tokens/roles, OpenIddict client secrets and JSON settings, scopes, authorizations, tokens, and historical migration metadata remain in their existing tables.
- The live schema takes precedence over stale creation scripts: User.CreationTime has `DEFAULT CURRENT_TIMESTAMP`; authorization Type is varchar(50); token Type is varchar(150). The application maps these exact types, pins Identity schema version 1, and tolerates legacy SearchUsers objects in a restored dump. Fresh AIS3 databases defer custom search objects until that feature is designed.
- A fresh database created from Scripts/CreateSchema.sql matches the AIS2 table/constraint/standard-index baseline. PopulateSchema.sql is empty; legacy search objects are excluded from fresh-schema requirements. Restored data/history are left unchanged, and runtime behavior does not depend on local EF migrations. Catalog comparison remains test-only.
- A legacy Identity V3 password-hash fixture verifies successfully, and newly created OpenIddict client secrets round-trip through the store. Existing user passwords/client secrets were not supplied, so actual migrated sign-in and protocol flows remain Steps 3/4 and the migration rehearsal.
- Persisted token rows being readable does not establish session/token continuity across cutover: signing/encryption and Data Protection keys must still be reviewed.

## Step 4 protocol checks

- Preserves all four AIS2 endpoint paths, code/refresh flows, implicit consent, client-specific PKCE requirements, secrets, registered redirects, and grant/response/scope permissions. Endpoint permissions remain ignored because existing AIS2 client records omit them. Legacy confidential-client and public PKCE fixtures are exercised against the real stores.
- Preserves `sub` as user ID, `name` as username/email, profile fields, and AIS2 standard/custom scope claim selection. Custom scopes still use their `claims` JSON property and resources. Security stamps and protocol-reserved claims cannot be exported through scope configuration. Userinfo now returns the current allowed claims for a valid access token.
- Intentional changes include S256-only PKCE, no implicit login from arbitrary preview parameters, protected/expiring login continuation, confirmation plus antiforgery for logout, account/stamp checks on code exchange and refresh, current claims at renewal, and rotating refresh tokens with fixed expiry and replay rejection.
- A separate standards-based local test client completed sign-in, verified the ID token, renewed tokens, and completed logout. Persisted AIS2 data remains readable without migration; existing real client credentials and old tokens were not used. Representative deployed clients, keys, and cutover continuity still need their migration rehearsal.

## Step 5 registration and mail

- Retains Identity user IDs/password hashes and the existing profile fields. Screen name defaults to first/last name, bounded to the existing 255-character field. New registrations have confirmed-email sign-in requirements, lockout enabled, and no administrator claim. Existing/ambiguous legacy email records are preserved; ambiguous recipients are not guessed.
- Confirmation uses a purpose-specific AIS3 Identity/Data Protection token with a 24-hour lifetime. Confirmation requires POST/antiforgery and rotates the stamp; no automatic login is created. AIS2-issued email tokens are not promised to survive the switch.
- AliceMailService's wire contract is unchanged: a MessagePack `List<byte[]>`, with each byte array a complete MIME message. The sender produces text and HTML confirmation bodies, publishes persistent messages to the durable default-exchange queue, and awaits mandatory publisher confirmation. Existing AliceMailService code consumed the message and delivered it to local SMTP during verification.
- No schema, SQL, migration, or AliceMailService source changes. Mail operations and real deployment configuration remain Step 12; the host remains Development/Testing-only.

## Step 14 migration rehearsal

Steps 1–13, including staging hosting, are now approved and committed. The [Step 14 review](reviews/step-14.md) records the current migration evidence; the earlier step sections above describe their original verification boundaries.

- The local AIS2 snapshot's 12 public tables, two identity sequences and shared catalog match disposable restores, including hashes, stamps, client settings, scopes and old protocol rows. Applying PopulateSchema.sql preserves data and migration history. No conversion or schema upgrade is needed.
- Synthetic legacy-compatible accounts and clients cross a dump/restore boundary before direct/admin sign-in and public S256/confidential OIDC verification. Real deployed credentials and client expectations remain manual review items.
- AIS3's separate cookie/key-ring purposes and required `auth_time` mean a database/certificate restore alone does not establish old-session/grant continuity. The [migration guide](migration.md) proposes planned reauthentication and separate AIS3-owned keys for approval, while preserving AIS2 state for rollback.
- Production cutover remains Step 15; Step 14 changes stay uncommitted pending manual review.
