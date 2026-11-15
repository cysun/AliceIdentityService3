import type { Plugin } from 'vite';

// Public account/static routes go to ASP.NET Core. Everything Vite itself serves
// is admin preview content and must pass the backend's real cookie/claim policy.
export const backendRoutes =
  '^/(Account(?:/|$|\\?)|api(?:/|$|\\?)|connect(?:/|$|\\?)|health(?:/|$|\\?)|css/|js/|brand/|$)';

export function adminAccess(): Plugin {
  return {
    name: 'ais-admin-access',
    configureServer(server) {
      server.middlewares.use(async (request, response, next) => {
        const url = request.url ?? '/';
        if (new RegExp(backendRoutes).test(url)) return next();
        response.setHeader('Cache-Control', 'no-store');
        try {
          const access = await fetch('http://127.0.0.1:5442/api/system/info', {
            headers: {
              cookie: request.headers.cookie ?? '',
              'X-Forwarded-Proto': 'https',
            },
            redirect: 'manual',
            signal: AbortSignal.timeout(5000),
          });
          if (access.ok) return next();
          if (access.status === 401 || access.status === 403) {
            // Never reflect a protocol-relative request target into a login redirect.
            const returnUrl = url.startsWith('/') && !url.startsWith('//') ? url : '/admin/';
            response.statusCode = 302;
            response.setHeader(
              'Location',
              access.status === 401
                ? `/Account/Login?ReturnUrl=${encodeURIComponent(returnUrl)}`
                : '/Account/AccessDenied',
            );
            response.end();
            return;
          }
        } catch {
          // Keep the preview closed while the backend starts or reloads.
        }
        response.statusCode = 503;
        response.end('AIS is unavailable. Please try again shortly.');
      });
    },
  };
}
