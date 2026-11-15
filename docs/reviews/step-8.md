# Step 8 review — User reading and search

Steps 1–7 are approved and committed (Step 7: `ebc1ba8`). Step 8 is implemented and uncommitted for manual review. Stop before Step 9.

## Delivered behavior

- The approved React/Base UI user directory and Profile/Claims/Security tabs read live data from the single ASP.NET Core backend. The server-rendered account pages and existing cookie/admin policy are preserved.
- Case-insensitive substring search covers first name, last name, screen name, and email. Every whitespace-separated term must match somewhere, in any order. For example, `smith john` finds Maryjohn Smith even if the screen name is Navigator. `%`, `_`, and backslash are literal characters. Accent folding, fuzzy matching and automatic spell correction are not enabled. Short/Unicode names work.
- Search/filter/sort/page/page-size state is stored in the URL and restored on browser navigation. Typing waits 300 ms before requesting data; obsolete requests are cancelled and cannot replace newer results. Focus stays in the search field. Empty, loading, missing-account, session-denied and service-failure states contain no sample fallback.
- The list defaults to 25 users per page, with 50/100 options. Sorting by User, Status or Joined happens in PostgreSQL, with ID ascending as a tie-breaker. Filter/sort/page-size changes return to page one. A page beyond the remaining results resolves to the last page. Details preserve the list URL in the All users link.
- Confirmed/Unconfirmed use the email-confirmation flag, including locked accounts. Locked means lockout is enabled and the end time is still in the future. Badges show Locked first, otherwise confirmation status. Dashboard attention links use these same filters; recent registrations open real details.
- Details expose profile fields, assigned claim type/value pairs and security summaries. Claims are escaped and read-only. Password hashes, security/concurrency stamps and login/token secrets are excluded from response contracts. Administrator membership means an assigned `ais_admin` claim with any value, including duplicate claims.
- Account editing, deletion, password/lockout operations and claim assignment are not introduced. They retain their Step 9/10 review gates.

## SQL preparation

For an existing **test AIS3 database or separate AIS2 restore**, stop its application and apply:

```sh
psql -X -h /tmp -d ais3 -v ON_ERROR_STOP=1 -f AliceIdentityService/Scripts/PopulateSchema.sql
```

Replace `ais3` with the intended test database. Use its owner/deployment role. The PostgreSQL installation must supply `pg_trgm`; this script installs it in `public` and creates `IX_AspNetUsers_Search`. It can be rerun and does not change existing rows, Identity tables/columns, standard indexes, migration history, or AIS2's legacy search objects. Ordinary index creation blocks writes while building, so run the script while the test application is stopped. See the [database guide](../database.md) for installation checks and the extension-schema assumption.

For a fresh database, first apply CreateSchema.sql, then PopulateSchema.sql, then bootstrap as documented in the README. **Never apply CreateSchema.sql over an AIS2 restore or an existing AIS3 database.** The generated CreateSchema.sql is unchanged in Step 8. Private appsettings.json and migrations remain ignored; appsettings.sample.json remains tracked and unchanged.

## Manual review

1. Configure the existing private settings for your prepared test database. Use an existing confirmed administrator and a separate confirmed ordinary account. A representative review database should include more than 25 users, unconfirmed and locked accounts, duplicate names/dates, a screen name different from the real name, and a short or Unicode name.
2. Build and run the same ASP.NET Core project, or use the existing Vite development runner:

   ```sh
   npm run build --prefix AliceIdentityService.Admin
   dotnet run --project AliceIdentityService
   # Alternatively, after stopping the server above:
   npm run dev --prefix AliceIdentityService.Admin
   ```

   Open https://localhost:7443/admin/users. Rider can serve this same built SPA. Check the signed-in identity, real rows/counts, absence of sample labels, and the retained visual layout.
3. Search a middle fragment of a name, a screen name, an email fragment, and multiple terms in reversed order. Check mixed case, `Li` or another short name, Unicode, literal `%`/`_`/backslash, empty input, and no matches. A search for `jose` does not fold the accent in `José`. Pause while typing and continue: the field must keep focus. Change terms quickly: old results must not overwrite new results.
4. Choose each status filter. An unconfirmed locked account belongs to both the Unconfirmed and Locked filters and displays a Locked badge. A disabled or expired lockout does not belong to Locked. Compare attention links/counts from Overview. Click a recent registration to open its live detail.
5. With multiple pages, use Next/Previous, change page size, and toggle each sort header. Check stable ties and that filtering/sorting resets to page one. Reload a bookmarked URL and use browser Back/Forward. Click a user, then All users: the prior list query/filter/sort/page must be preserved.
6. Review Profile, Claims and Security for an account. Compare its stored names/email/confirmation/creation, administrator membership, claims, password presence, two-factor and lockout summaries. Names/claims containing markup must display as text. No Save/Delete/Reset/Unlock/Add claim controls should be present. Refresh reflects subsequent test-data changes. An unknown `/admin/users/<id>` shows User not found inside the authorized shell.
7. In browser developer tools, inspect the list/detail JSON. Only the documented summary/profile/claim/security fields should appear; no hashes/stamps/token values or complete Identity entities. API responses must have `Cache-Control: no-store` and use the same-origin cookie.
8. Signed out, `/api/admin/users` and `/api/admin/users/<id>` return 401. A signed-in ordinary account receives 403; neither response redirects to login HTML. With the SPA open, sign out in another tab or remove the administrator claim in the test database, then focus/navigate/refresh: protected content must clear. Existing fixed cookie expiry and security-stamp invalidation continue to apply.
9. For a failure/retry check, stop only your test server while the directory is open, then Refresh. Expect the unavailable/retry boundary with previous data cleared. Restart and retry. Do not stop shared PostgreSQL or RabbitMQ services.
10. Stop the verification server. Review the uncommitted changes before authorizing Step 9.

## API contract

- `GET /api/admin/users?q=smith+john&status=Confirmed&sort=name&direction=asc&page=1&pageSize=25`
- `GET /api/admin/users/{id}` returns 404 for an unknown account.
- `q`: optional, at most 256 characters/eight distinct whitespace-separated terms. All terms are ANDed. Empty/whitespace lists users without a search predicate.
- `status`: omitted, Confirmed, Unconfirmed or Locked. `sort`: name (default), status or joined. `direction`: asc (default) or desc.
- `page`: 1–10,000. `pageSize`: 1–100, default 25. Invalid API inputs return 400; UI bookmarks normalize invalid control values and display an error for oversized search input.
- List response: `items`, `totalCount`, effective `page`, `pageSize`, `asOf`. Counts and items use one repeatable-read snapshot; detail reads use a consistent snapshot too. Time-dependent lockout evaluation uses one server time per request.
- List items: ID, display name, email, effective status, creation time and administrator boolean. Detail adds first/last/screen names, email confirmation, claim type/value pairs and boolean/count/date security summaries.

## Automated checks

```sh
npm run check --prefix AliceIdentityService.Admin
AIS3_TEST_ADMIN_CONNECTION='Host=/tmp;Database=postgres;Username=cysun' \
AIS3_TEST_RESTORED_CONNECTION='Host=/tmp;Database=ais3_copy;Username=cysun;Options=-c default_transaction_read_only=on' \
AIS3_TEST_RABBITMQ_HOST=localhost \
dotnet test
dotnet format --verify-no-changes --no-restore
git diff --check
```

Adjust connection settings for your machine. Apply PopulateSchema.sql to the separate restore before using the read-only test connection. The admin connection creates/drops disposable databases; RabbitMQ tests use their own random queues and send no external SMTP mail. Integration tests skip when the enabling variables are absent. Build the frontend before backend tests so HTTP checks can inspect its actual assets.

Focused commands:

```sh
# Supply the PostgreSQL environment variables above.
dotnet test --filter 'FullyQualifiedName~AdminUserTests|FullyQualifiedName~AdminTests'
npm test --prefix AliceIdentityService.Admin -- src/pages/users.test.tsx src/pages/admin.test.tsx
```

The search-plan test captures the real EF count and paginated SELECT commands and parameters, then runs EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) on 30,000 synthetic users. Both selective queries must use IX_AspNetUsers_Search without forcing planner settings. Short/common search and deep paging are also exercised. PostgreSQL may correctly scan small tables or broad/short patterns; there is no requirement that every search use the index. Default browsing/sorting still has costs and can be profiled if the directory grows.

## Verification — 2026-11-15

- Full backend suite: 92 passed, none failed/skipped, including fresh PostgreSQL databases, a read-only AIS2 restore with the additive SQL installed, and isolated RabbitMQ checks.
- Full frontend suite: 44 passed, with formatting, lint, TypeScript and Vite build checks.
- The versioned SQL applied twice to both a fresh database and a separate AIS2 restore. Shared schema compatibility passed, and the actual selective count/page queries used the GIN index on the 30,000-user fixture.
- Browser review used the compiled SPA served by one temporary HTTPS ASP.NET Core host and 106 fictional accounts. Checked paging, substring/multi-term search with maintained focus, confirmation filters, page size, date sorting, profile/claims/security tabs, literal markup, and preserved return navigation. No browser console errors were reported.
- C# formatting verification and the solution build passed with zero warnings/errors. All 12 tables in the upgraded disposable AIS2 restore had identical row contents to the source. The browser verification host was stopped and both disposable review databases were removed. No production database or existing AIS2/AIS3 database was modified; no external email was sent. Step 9 has not started.
