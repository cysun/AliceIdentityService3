# Step 1 review

> Historical Step 1 record. For current project paths and commands, use the [README](../../README.md) and [Step 2 checklist](step-2.md).

Status: approved by the user for commit, including the reviewed account copy and branding changes. **Step 2 has not started; its configuration and database compatibility requirements are being discussed.**

## Run

Follow the root README (`npm ci`, trust local HTTPS if needed, `npm run dev`). Open https://localhost:7443. Use fictitious values only. No external services or database are required.

## Manual acceptance checklist

- [ ] Direct `/Account/Login`: split layout on desktop and single form on mobile.
- [ ] OIDC preview link: centered form with “Sign in to continue to Campus Portal”; no left introduction.
- [ ] Register and forgot-password links preserve the compact application context, then return to compact login.
- [ ] Direct registration/recovery retain the full account layout.
- [ ] Show/hide password and account action buttons work locally; actions explain no request was sent.
- [ ] Account page network traffic includes its native script/CSS, with no React or admin bundles.
- [ ] `/admin/`: sidebar, overview, sample indicators and actual local API version are visible.
- [ ] Users: search by name/email, sort column headers, use the status filter, open details, switch tabs.
- [ ] Dashboard attention links open the relevant filtered user list.
- [ ] Components: invalid form values show errors; valid sample confirms locally; Reset clears it.
- [ ] Dialog: keyboard focus stays inside while open; Escape closes it and returns focus to trigger.
- [ ] Light/dark system themes, mobile navigation and narrow layouts remain usable. Tables may scroll horizontally within their container.
- [ ] Reload `/admin/users/alex` directly, including in the built .NET host.
- [ ] Ctrl+C stops both local development processes; the built host then starts on the same port.

## Automated checks

`npm run check`: formatting, lint, frontend type/build checks, .NET build, five frontend interaction tests, and eighteen HTTP/host tests. The host tests cover Razor presentation/context, absent React assets in account HTML, unrecognized preview context, method restrictions, API metadata/health, scoped SPA routing and the Production guard.

Browser verification additionally checks live HTTPS routing, desktop/mobile layouts, account requests, user filtering, dialog behavior, and built/static delivery. Results are recorded after verification below.

## Review boundary

No database schema, Identity/OpenIddict services, real login, real account mutation, email, or migration is part of this handoff. Clients/scopes links intentionally identify their future implementation steps. The user detail view is a read-only sample. Production startup is disabled.

## Verification record — 2026-11-14

- Clean `npm ci` in the final repository succeeded; npm reported zero known vulnerabilities for the installed lockfile.
- `npm run check` passed. After the final routing additions, xUnit passed **18/18** tests; Vitest passed **5/5** interaction tests. TypeScript, Oxlint, Prettier, and .NET builds passed with no build warnings.
- `dotnet format --verify-no-changes` passed. The Console `info` command returned the shared AIS3 preview metadata.
- Live Chromium checks passed for account layouts/context, absence of React downloads on account pages, no credential POST, local API metadata, status filtering/search, user detail reloads, validation and dialog focus/escape behavior.
- Seven account/admin routes were checked at a 390 px viewport with no document overflow. Desktop, mobile and light/dark screenshots were visually inspected. Tables scroll inside their container on narrow screens.
- The same browser checks passed against `artifacts/preview` served by the published .NET host alone. Build output includes Razor views and all required admin/static assets.
- The development runner waits for backend health before starting Vite. Ctrl+C was verified to stop the backend, watcher and frontend. The macOS SDK manifest-path issue is addressed by the forward-slash intermediate directory setting.
- Browser automation accepted the local development certificate for testing. Trust it locally using the README command if your browser prompts; certificate trust was not changed by the implementation.

These checks validate Step 1's scaffold and preview behavior. Real Identity/OIDC behavior, database integration and production deployment are outside this review.
