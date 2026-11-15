import type { IncomingMessage, ServerResponse } from 'node:http';
import type { Connect, ViteDevServer } from 'vite';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { adminAccess } from './admin-access.js';

afterEach(() => vi.unstubAllGlobals());

function middleware() {
  let handle!: Connect.NextHandleFunction;
  const hook = adminAccess().configureServer;
  if (typeof hook !== 'function') throw new Error('Expected a configureServer hook');
  hook.call(
    {} as never,
    {
      middlewares: { use: (handler: Connect.NextHandleFunction) => (handle = handler) },
    } as unknown as ViteDevServer,
  );
  return handle;
}

async function request(url: string, status: number | Error) {
  const fetch = vi.fn();
  if (status instanceof Error) fetch.mockRejectedValue(status);
  else fetch.mockResolvedValue(new Response(null, { status }));
  vi.stubGlobal('fetch', fetch);
  const response = { setHeader: vi.fn(), end: vi.fn(), statusCode: 200 };
  const next = vi.fn();
  await middleware()(
    { url, headers: { cookie: 'test-session' } } as IncomingMessage,
    response as unknown as ServerResponse,
    next,
  );
  return { response, next, fetch };
}

describe('development admin access', () => {
  it.each([
    '/Account/Login?ReturnUrl=%2Fadmin%2F',
    '/.well-known/openid-configuration',
    '/.well-known/jwks',
  ])('leaves backend route %s to the proxy', async (url) => {
    const { next, fetch } = await request(url, 401);
    expect(next).toHaveBeenCalledOnce();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('asks the backend to validate the cookie before serving admin content', async () => {
    const { next, fetch } = await request('/admin/users', 200);
    expect(next).toHaveBeenCalledOnce();
    expect(fetch).toHaveBeenCalledWith(
      'http://127.0.0.1:5442/api/system/info',
      expect.objectContaining({
        headers: { cookie: 'test-session', 'X-Forwarded-Proto': 'https' },
        redirect: 'manual',
      }),
    );
  });

  it.each(['/admin/users', '/admin/src/App.tsx', '/index.html', '/API/system/info'])(
    'challenges anonymous requests to %s without serving content',
    async (url) => {
      const { response, next } = await request(url, 401);
      expect(next).not.toHaveBeenCalled();
      expect(response.statusCode).toBe(302);
      expect(response.setHeader).toHaveBeenCalledWith(
        'Location',
        `/Account/Login?ReturnUrl=${encodeURIComponent(url)}`,
      );
      expect(response.setHeader).toHaveBeenCalledWith('Cache-Control', 'no-store');
    },
  );

  it('sends non-administrators to access denied', async () => {
    const { response, next } = await request('/admin/', 403);
    expect(next).not.toHaveBeenCalled();
    expect(response.setHeader).toHaveBeenCalledWith('Location', '/Account/AccessDenied');
  });

  it.each([500, 302, new Error('offline')])(
    'fails closed if the backend cannot validate access (%s)',
    async (status) => {
      const { response, next } = await request('/admin/', status);
      expect(next).not.toHaveBeenCalled();
      expect(response.statusCode).toBe(503);
    },
  );
});
