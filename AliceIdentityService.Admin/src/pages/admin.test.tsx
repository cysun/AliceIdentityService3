import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { AdminShell } from '@/App';
import { AdminAccess } from '@/lib/admin-session';
import type { AdminOverview, AdminSession } from '@/lib/admin-api';

function session(lifetimeMs = 8 * 60 * 60 * 1000): AdminSession {
  const now = new Date('2026-11-15T12:00:00Z');
  return {
    user: {
      id: 'real-admin',
      email: 'admin@example.test',
      firstName: 'Actual',
      lastName: 'Admin',
      screenName: 'Actual Administrator',
    },
    serverTime: now.toISOString(),
    expiresAt: new Date(now.getTime() + lifetimeMs).toISOString(),
    application: {
      name: 'Alice Identity Service',
      version: '3.0.0-preview.7',
      implementationStep: 7,
    },
  };
}
const overview: AdminOverview = {
  counts: {
    totalUsers: 42,
    connectedApplications: 9,
    unconfirmedUsers: 3,
    administrators: 2,
    lockedUsers: 0,
  },
  recentRegistrations: [
    {
      id: 'real-user',
      name: 'Recent <person>',
      email: 'recent@example.test',
      status: 'Unconfirmed',
      createdAt: '2026-11-14T12:00:00Z',
    },
  ],
  applications: [{ id: 'real-client', name: 'Actual Portal', clientId: 'portal-client' }],
  asOf: '2026-11-15T12:00:00Z',
};
const json = (value: unknown, status = 200) =>
  new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}
function api(
  sessionResponse = () => Promise.resolve(json(session())),
  overviewResponse = () => Promise.resolve(json(overview)),
) {
  const fetch = vi.fn((url: string) => {
    if (url === '/api/admin/session') return sessionResponse();
    if (url === '/api/admin/overview') return overviewResponse();
    throw new Error('Unexpected API path: ' + url);
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
function mount(path = '/') {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AdminAccess>
        <AdminShell />
      </AdminAccess>
    </MemoryRouter>,
  );
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('live administration', () => {
  it('waits for authorization and then renders the real identity and bounded overview without sample data', async () => {
    const pending = deferred<Response>();
    const fetch = api(() => pending.promise);
    mount();
    expect(screen.getByText('Checking your session…')).toBeInTheDocument();
    expect(screen.queryByText('Alex Morgan')).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    pending.resolve(json(session()));
    expect(await screen.findByText('Actual Administrator')).toBeInTheDocument();
    expect(await screen.findByText('Recent <person>')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Total users' })).toHaveTextContent('42');
    expect(screen.getByRole('region', { name: 'Connected applications' })).toHaveTextContent('9');
    expect(screen.getByRole('region', { name: 'Administrators' })).toHaveTextContent('2');
    expect(screen.getByText('0 locked accounts')).toBeInTheDocument();
    expect(screen.getByText('Actual Portal')).toBeInTheDocument();
    expect(screen.queryByText('Alex Morgan')).not.toBeInTheDocument();
    expect(screen.queryByText('Campus Portal')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Recent/ })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Your account' })).toHaveAttribute('href', '/Account');
    expect(screen.getByRole('link', { name: 'Sign out' })).toHaveAttribute(
      'href',
      '/Account/Logout',
    );
    expect(fetch).toHaveBeenCalledWith(
      '/api/admin/session',
      expect.objectContaining({ credentials: 'same-origin', cache: 'no-store', redirect: 'error' }),
    );
  });

  it('shows empty overview states and refreshes actual values', async () => {
    let result: AdminOverview = {
      ...overview,
      counts: {
        totalUsers: 0,
        connectedApplications: 0,
        unconfirmedUsers: 0,
        administrators: 0,
        lockedUsers: 0,
      },
      recentRegistrations: [],
      applications: [],
    };
    api(undefined, async () => json(result));
    mount();
    expect(await screen.findByText('No accounts have registered yet.')).toBeInTheDocument();
    expect(screen.getByText('No applications are registered yet.')).toBeInTheDocument();
    result = overview;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }));
    expect(await screen.findByText('Actual Portal')).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Total users' })).toHaveTextContent('42');
  });

  it('keeps user browsing clearly labeled as sample data and rechecks access on route changes', async () => {
    const fetch = api();
    mount();
    await screen.findByText('Actual Administrator');
    await userEvent.setup().click(screen.getByRole('link', { name: 'Users' }));
    expect(await screen.findByText('Fictional sample data')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Users' })).toBeInTheDocument();
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/session')).toHaveLength(2);
    expect(
      fetch.mock.calls.every(
        ([url]) => url === '/api/admin/session' || url === '/api/admin/overview',
      ),
    ).toBe(true);
  });

  it.each([401, 403])('never renders protected routes after session status %s', async (status) => {
    const fetch = api(async () => json({}, status));
    mount('/users?status=Locked');
    expect(await screen.findByRole('alert')).toHaveTextContent(
      status === 401 ? 'Please sign in again' : 'Administrator access required',
    );
    expect(screen.queryByRole('navigation', { name: 'Administration' })).not.toBeInTheDocument();
    expect(screen.queryByText('Alex Morgan')).not.toBeInTheDocument();
    expect(fetch).toHaveBeenCalledTimes(1);
    if (status === 401)
      expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute(
        'href',
        '/Account/Login?returnUrl=%2Fadmin%2Fusers%3Fstatus%3DLocked',
      );
  });

  it.each([401, 403])(
    'clears the shell when a later overview request returns %s',
    async (status) => {
      let denied = false;
      api(undefined, async () => (denied ? json({}, status) : json(overview)));
      mount();
      await screen.findByText('Actual Portal');
      denied = true;
      await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }));
      expect(await screen.findByRole('alert')).toHaveTextContent(
        status === 401 ? 'Please sign in again' : 'Administrator access required',
      );
      expect(screen.queryByText('Actual Administrator')).not.toBeInTheDocument();
      expect(screen.queryByText('Actual Portal')).not.toBeInTheDocument();
    },
  );

  it.each(['focus', 'pageshow', 'visibilitychange'])(
    'revalidates on %s and removes data after another tab signs out',
    async (event) => {
      let signedOut = false;
      vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
      api(async () => (signedOut ? json({}, 401) : json(session())));
      mount();
      await screen.findByText('Actual Portal');
      signedOut = true;
      fireEvent(event === 'visibilitychange' ? document : window, new Event(event));
      expect(
        await screen.findByRole('heading', { name: 'Please sign in again' }),
      ).toBeInTheDocument();
      expect(screen.queryByText('Actual Portal')).not.toBeInTheDocument();
    },
  );

  it('expires locally at the server-provided lifetime even when the workstation clock differs', async () => {
    vi.useFakeTimers({ now: new Date('2000-01-01T00:00:00Z') });
    api(async () => json(session(10_000)));
    mount();
    await act(async () => {});
    expect(screen.getByText('Actual Administrator')).toBeInTheDocument();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_001);
    });
    expect(screen.getByRole('heading', { name: 'Please sign in again' })).toBeInTheDocument();
    expect(screen.queryByText('Actual Portal')).not.toBeInTheDocument();
  });

  it('polls visible sessions without resetting the overview and detects removed privileges', async () => {
    vi.useFakeTimers();
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    let forbidden = false;
    const fetch = api(async () => (forbidden ? json({}, 403) : json(session())));
    mount();
    await act(async () => {});
    expect(screen.getByText('Actual Administrator')).toBeInTheDocument();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/overview')).toHaveLength(1);
    forbidden = true;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(
      screen.getByRole('heading', { name: 'Administrator access required' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Actual Portal')).not.toBeInTheDocument();
  });

  it.each(['network', 'server', 'html'])(
    'fails closed on a %s failure and supports a clean retry',
    async (failure) => {
      let failed = true;
      api(async () => {
        if (!failed) return json(session());
        if (failure === 'network') throw new TypeError('Offline');
        return failure === 'html'
          ? new Response('<html>Login</html>', { headers: { 'Content-Type': 'text/html' } })
          : json({}, 503);
      });
      mount();
      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Administration is temporarily unavailable',
      );
      expect(screen.queryByRole('navigation')).not.toBeInTheDocument();
      failed = false;
      await userEvent.setup().click(screen.getByRole('button', { name: 'Try again' }));
      expect(await screen.findByText('Actual Portal')).toBeInTheDocument();
    },
  );

  it('does not restore stale authorization when an older session check completes after a rejected data request', async () => {
    const pending = deferred<Response>();
    let checkingAgain = false;
    let denied = false;
    api(
      () => (checkingAgain ? pending.promise : Promise.resolve(json(session()))),
      async () => (denied ? json({}, 401) : json(overview)),
    );
    mount();
    await screen.findByText('Actual Portal');
    checkingAgain = true;
    fireEvent(window, new Event('focus'));
    denied = true;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }));
    await screen.findByRole('heading', { name: 'Please sign in again' });
    await act(async () => {
      pending.resolve(json(session()));
    });
    expect(screen.queryByText('Actual Administrator')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Please sign in again' })).toBeInTheDocument();
  });

  it('reloads overview data if another administrator signs in in the same browser', async () => {
    let changed = false;
    const next = session();
    next.user = { ...next.user, id: 'next-admin', screenName: 'Next Administrator' };
    const fetch = api(
      async () => json(changed ? next : session()),
      async () => json(changed ? { ...overview, recentRegistrations: [] } : overview),
    );
    mount();
    await screen.findByText('Recent <person>');
    changed = true;
    fireEvent(window, new Event('focus'));
    expect(await screen.findByText('Next Administrator')).toBeInTheDocument();
    expect(await screen.findByText('No accounts have registered yet.')).toBeInTheDocument();
    expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/overview')).toHaveLength(2);
    expect(
      within(screen.getByRole('complementary')).getByText('Next Administrator'),
    ).toBeInTheDocument();
  });
});
