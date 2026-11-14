# Alice Identity Service 3 (AIS3)

A fresh implementation of Alice Identity Service. **Step 1 is approved:** a runnable .NET host, server-rendered account previews, and a React administration preview. All people and applications shown are fictional. No database, authentication, token issuance, email delivery, or account mutation is implemented yet.

## Quick start

Requirements: .NET SDK **10.0.404** (or a later 10.0.4xx patch), Node.js **24 or newer**, and npm. `global.json` selects .NET 10 even if another SDK is the machine default. npm and NuGet lockfiles are included.

From this repository:

```sh
npm ci
# Once on your development machine, if the HTTPS certificate is not trusted:
dotnet dev-certs https --trust
npm run dev
```

Open **https://localhost:7443**. Vite serves the admin UI and proxies account/API requests to the .NET host on `127.0.0.1:5442`, keeping the browser on a single HTTPS origin. Both processes reload during development; Ctrl+C stops both. The dev runner exports your local development certificate into ignored `.dev/certs/`; it never copies that certificate into published output.

| Preview             | URL                                                                 |
| ------------------- | ------------------------------------------------------------------- |
| Direct login        | https://localhost:7443/Account/Login                                |
| Compact OIDC login  | https://localhost:7443/Account/Login?preview=oidc&app=campus-portal |
| Register            | https://localhost:7443/Account/Register                             |
| Forgot password     | https://localhost:7443/Account/ForgotPassword                       |
| Admin overview      | https://localhost:7443/admin/                                       |
| User list / details | https://localhost:7443/admin/users                                  |
| Component samples   | https://localhost:7443/admin/components                             |

The OIDC query parameters are **visual preview controls only**. They do not validate an authorization request. Step 4 will derive application context from a validated OpenIddict request. Account actions show a preview message and do not submit credentials. Use sample values only.

## Build and check

```sh
npm run check
```

Runs Prettier, Oxlint, TypeScript/Vite, .NET build, Vitest/React Testing Library, and xUnit HTTP integration tests. Build the frontend before running standalone .NET tests: a routing test deliberately checks the real generated admin entry point.

```sh
npm run format
# Format C# separately:
dotnet format AliceIdentityService3.sln --no-restore
# A single .NET process serving Razor, API, and built React assets:
npm run build
dotnet run --project src/AliceIdentityService.Web --launch-profile https
# Console scaffold:
dotnet run --project src/AliceIdentityService.Console -- info
```

Stop the dev runner before starting the built host; both use port 7443. The optional mobile/WASM SDK workload resolver is disabled for this web/console solution; those workloads are not needed.

To inspect the deployable shape:

```sh
npm run publish:preview
cd artifacts/preview
ASPNETCORE_ENVIRONMENT=Development dotnet AliceIdentityService.Web.dll --urls https://localhost:7443
```

The host intentionally refuses to start in Production or Staging during Step 1. Setting Development permits this **unauthenticated local preview**, not a deployable identity service. The publication check only verifies that one ASP.NET Core host can serve all assets; no Node runtime is needed to run published output.

## Projects

```text
AliceIdentityService3.sln
src/
  AliceIdentityService.Core/       Shared application layer; metadata only for now
  AliceIdentityService.Web/        ASP.NET Core host, MVC/Razor accounts, API, static admin
  AliceIdentityService.Admin/      React + TypeScript + Vite
  AliceIdentityService.Console/    Administrative CLI scaffold
tests/
  AliceIdentityService.Tests/      xUnit HTTP integration tests
scripts/dev.mjs                   Starts and stops the local frontend/backend together
docs/
  architecture.md
  compatibility-baseline.md
  implementation-plan.md
  reviews/step-1.md
  design/approved-mockup.html
```

Web and Console reference Core; Console does not reference Web. React is a build workspace and writes generated files to Web's ignored `wwwroot/admin/`. The solution includes the four .NET projects; root npm scripts orchestrate both toolchains.

The React application and its dependencies are declared in `src/AliceIdentityService.Admin/package.json`. The root `package.json` is a persistent workspace and task runner, not a second frontend or a temporary Step 1 app. It provides one install/lockfile and commands for development, formatting, builds, tests, and publishing. npm may install shared dependencies into the root `node_modules` directory. These are development/build dependencies; the published .NET host runs without Node.js. Account pages remain Razor views with small native scripts in the Web project.

UI: shadcn/ui **Base UI** components, Tailwind CSS 4, Lucide icons, TanStack Table, React Hook Form, and Zod. Razor and React share color/typography tokens. The account page uses only a small native script; it does not download the React bundle. Both interfaces follow the system light/dark preference.

Start with the [Step 1 review checklist](docs/reviews/step-1.md). See the [architecture](docs/architecture.md), [compatibility baseline](docs/compatibility-baseline.md), and [implementation plan](docs/implementation-plan.md). **Step 1 is approved. Step 2 configuration and database compatibility are being discussed before implementation.**
