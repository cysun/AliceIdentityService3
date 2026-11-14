# AIS3 architecture

## Host and boundaries

AIS3 will run as one ASP.NET Core application with server-rendered MVC/Razor account pages and a React administration area. The same host will own Identity, OIDC endpoints, and an admin API. This keeps account navigation and cookies on one origin and avoids downloading React for login. A separate API deployment is not required.

- `Core`: shared application types and, in later steps, domain/application services and persistence abstractions or implementation organized by feature. The Step 1 metadata record deliberately avoids premature authentication/data models.
- `Web`: HTTP host, Razor account UI, API endpoints, and eventually Identity/OpenIddict wiring. Controllers coordinate application operations; they will not be reused by Console.
- `Console`: administrative entry point depending on Core. Step 2 adds database initialization and first-administrator setup.
- `Admin`: React browser application under `/admin/`; eventual API calls go to the same host under `/api/`. Step 1 fetches only `/api/system/info`.
- `Tests`: host integration tests plus colocated React interaction tests.

Razor views use shared CSS variables from `Web/wwwroot/css/tokens.css`; Tailwind imports those same tokens for React. React assets are built into `Web/wwwroot/admin` before .NET build/publish. Only `/admin/*` receives the SPA fallback. Unknown account/API/OIDC routes remain HTTP 404s.

## Account presentation

One account form/handler per action will support two presentation modes:

1. Direct visit: brand, explanatory left column, form on the right.
2. Validated OIDC visit: compact, centered form and “Sign in to continue to <application>.”

Step 1 previews the latter with an allowlisted fictional app slug. It is not a security boundary and does not process `returnUrl`, `client_id`, or a redirect URI. Step 4 must resolve the display name from a validated client and preserve validated authorization context across login/registration/reset. Never trust a query-supplied display name or redirect destination. The approved mockup uses similar preview controls solely for visual review.

## Authentication in later steps

Use ASP.NET Core Identity and OpenIddict, with PostgreSQL/EF Core and existing token contracts verified against AIS2. The admin browser uses same-origin server authentication; do not add token storage to localStorage. Admin authorization must be enforced server-side on every API operation. Implement antiforgery protections for cookie-authenticated mutations, safe return URL validation, POST-only mutations, and appropriate error handling as part of the relevant functional steps.

Step 1 intentionally has no authentication handlers, database connection, secrets, or admin mutation API. The Development/Testing-only host guard prevents its open preview routes from accidentally being used in a normal production environment. Replace that temporary guard with tested access controls as authentication is implemented.

## Frontend conventions

Generated shadcn components live in `Admin/src/components/ui`, using `@base-ui/react`. `components.json` selects `base-nova`. Generate future additions from the Admin directory with `npx shadcn@4.23.0 add <component>`, review generated imports, and retain Base UI. No Radix package is installed. The CLI itself is not a runtime dependency; the few Base UI state variants needed by these components are defined in `index.css`.

TanStack Table manages row rendering and sorting. Sample filtering is local; Step 8 will introduce API pagination/search with explicit server-side semantics. Forms use React Hook Form with Zod for client feedback; future server-side validation remains authoritative. The component preview is loaded on demand. All fixtures stay in `preview-data.ts` until replaced by the relevant live endpoints.
