import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
      version: '3.0.0-preview.9',
      implementationStep: 9,
    },
  };
}
const overview: AdminOverview = {
  counts: {
    totalUsers: 42,
    connectedApplications: 9,
    customScopes: 4,
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
  tokenResponse = () => Promise.resolve(json({ requestToken: 'sign-out-token' })),
) {
  const fetch = vi.fn((url: string) => {
    if (url === '/api/admin/session') return sessionResponse();
    if (url === '/api/account/antiforgery') return tokenResponse();
    if (url === '/api/admin/overview') return overviewResponse();
    if (url === '/api/admin/user-counts') return Promise.resolve(json(overview.counts));
    if (/^\/api\/admin\/(users|clients|scopes)\?/.test(url))
      return Promise.resolve(
        json({ items: [], totalCount: 0, page: 1, pageSize: 25, asOf: overview.asOf }),
      );
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
    const avatar = await screen.findByRole('button', {
      name: 'User menu for Actual Administrator',
    });
    expect(avatar).toHaveTextContent('AA');
    expect(screen.getByRole('banner')).toContainElement(avatar);
    const identity = within(screen.getByRole('complementary'));
    expect(identity.getByText('AA')).toBeVisible();
    expect(identity.getByText('Actual Administrator')).toBeVisible();
    expect(identity.getByText('Administrator')).toBeVisible();
    expect(identity.getByText('admin@example.test')).toBeVisible();
    expect(identity.queryByText('Sign out')).not.toBeInTheDocument();
    expect(screen.getByRole('contentinfo')).toHaveTextContent(
      'Alice Identity Service 3 · Copyright Chengyu Sun 2026',
    );
    expect(screen.getByRole('banner')).not.toHaveTextContent(session().application.version);
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Overview',
    );
    expect(await screen.findByText('Recent <person>')).toBeInTheDocument();
    const stats = within(screen.getByRole('navigation', { name: 'Overview statistics' }));
    expect(stats.getAllByRole('link')).toHaveLength(3);
    expect(stats.getByRole('link', { name: 'Total users 42' })).toHaveAttribute('href', '/users');
    expect(stats.getByRole('link', { name: 'Connected applications 9' })).toHaveAttribute(
      'href',
      '/clients',
    );
    expect(stats.getByRole('link', { name: 'Custom scopes 4' })).toHaveAttribute('href', '/scopes');
    expect(screen.getByText('0 locked accounts')).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Connected applications' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Actual Portal')).not.toBeInTheDocument();
    expect(screen.queryByText('Alex Morgan')).not.toBeInTheDocument();
    expect(screen.queryByText('Campus Portal')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Recent/ })).toHaveAttribute(
      'href',
      '/users/real-user',
    );
    const sidebar = within(screen.getByRole('navigation', { name: 'Administration' }));
    expect(sidebar.getAllByRole('link').map((item) => item.textContent)).toEqual([
      'Overview',
      'Users',
      'Clients',
      'Scopes',
      'Profile',
    ]);
    expect(sidebar.getByRole('link', { name: 'Profile' })).toHaveAttribute('href', '/profile');
    await userEvent.setup().click(avatar);
    const account = within(await screen.findByRole('menu', { name: 'User menu' }));
    expect(account.getByText('Actual Administrator')).toBeVisible();
    expect(account.getByText('admin@example.test')).toBeVisible();
    expect(account.getByText('Administrator')).toBeVisible();
    expect(fetch).toHaveBeenCalledWith(
      '/api/admin/session',
      expect.objectContaining({ credentials: 'same-origin', cache: 'no-store', redirect: 'error' }),
    );
  });

  it.each([200, 403])(
    'submits sign-out in one click with a fresh token for session status %s',
    async (status) => {
      api(async () => json(session(), status));
      const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
      mount();
      const user = userEvent.setup();
      if (status === 200)
        await user.click(
          await screen.findByRole('button', { name: 'User menu for Actual Administrator' }),
        );
      const control = await screen.findByRole(status === 200 ? 'menuitem' : 'button', {
        name: 'Sign out',
      });
      const form = control.closest('form')!;
      await user.click(control);
      expect(form).toHaveAttribute('method', 'post');
      expect(form).toHaveAttribute('action', '/Account/Logout');
      expect(new FormData(form).get('__RequestVerificationToken')).toBe('sign-out-token');
      expect(submit).toHaveBeenCalledTimes(1);
    },
  );

  it('finishes signing out if the avatar menu is closed while the token is loading', async () => {
    const pending = deferred<Response>();
    api(undefined, undefined, () => pending.promise);
    const submit = vi.spyOn(HTMLFormElement.prototype, 'submit').mockImplementation(() => {});
    const user = userEvent.setup();
    mount();
    await user.click(
      await screen.findByRole('button', { name: 'User menu for Actual Administrator' }),
    );
    await user.click(await screen.findByRole('menuitem', { name: 'Sign out' }));
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByRole('menu', { name: 'User menu' })).toBeNull());
    await act(async () => pending.resolve(json({ requestToken: 'fresh-token' })));
    expect(submit).toHaveBeenCalledTimes(1);
  });

  it('shows empty overview states and reads current counts on the next page load', async () => {
    let result: AdminOverview = {
      ...overview,
      counts: {
        totalUsers: 0,
        connectedApplications: 0,
        customScopes: 0,
        unconfirmedUsers: 0,
        administrators: 0,
        lockedUsers: 0,
      },
      recentRegistrations: [],
      applications: [],
    };
    api(undefined, async () => json(result));
    const view = mount();
    expect(await screen.findByText('No accounts have registered yet.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /refresh/i })).toBeNull();
    expect(screen.getByRole('link', { name: 'Connected applications 0' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Custom scopes 0' })).toBeVisible();
    result = overview;
    view.unmount();
    mount();
    expect(await screen.findByRole('link', { name: 'Total users 42' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Custom scopes 4' })).toBeVisible();
  });

  it.each([
    ['Total users 42', 'Users', 'Total', 'Create account'],
    ['Connected applications 9', 'Clients', 'No clients registered.', 'Register client'],
    ['Custom scopes 4', 'Scopes', 'No scopes defined.', 'Create scope'],
  ])('opens %s and rechecks access', async (card, directory, expectedText, createAction) => {
    const fetch = api();
    mount();
    await userEvent.setup().click(await screen.findByRole('link', { name: card }));
    expect(await screen.findByText(expectedText)).toBeInTheDocument();
    expect(screen.queryByText('Fictional sample data')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: directory })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: createAction })).toHaveClass('bg-primary');
    expect(screen.queryByRole('button', { name: /refresh/i })).toBeNull();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/' + directory,
    );
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/session')).toHaveLength(2);
    expect(
      fetch.mock.calls.every(
        ([url]) =>
          url === '/api/admin/session' ||
          url === '/api/admin/overview' ||
          (directory === 'Users'
            ? url === '/api/admin/user-counts'
            : url.startsWith('/api/admin/' + directory.toLowerCase() + '?')),
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
      const user = userEvent.setup();
      mount();
      await user.click(await screen.findByRole('link', { name: 'Total users 42' }));
      await screen.findByRole('heading', { name: 'Users' });
      denied = true;
      const sidebar = within(screen.getByRole('navigation', { name: 'Administration' }));
      await user.click(sidebar.getByRole('link', { name: 'Overview' }));
      expect(await screen.findByRole('alert')).toHaveTextContent(
        status === 401 ? 'Please sign in again' : 'Administrator access required',
      );
      expect(
        screen.queryByRole('button', { name: 'User menu for Actual Administrator' }),
      ).not.toBeInTheDocument();
      expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
    },
  );

  it.each(['focus', 'pageshow', 'visibilitychange'])(
    'revalidates on %s and removes data after another tab signs out',
    async (event) => {
      let signedOut = false;
      vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
      api(async () => (signedOut ? json({}, 401) : json(session())));
      mount();
      await screen.findByText('Recent <person>');
      signedOut = true;
      fireEvent(event === 'visibilitychange' ? document : window, new Event(event));
      expect(
        await screen.findByRole('heading', { name: 'Please sign in again' }),
      ).toBeInTheDocument();
      expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
    },
  );

  it('expires locally at the server-provided lifetime even when the workstation clock differs', async () => {
    vi.useFakeTimers({ now: new Date('2000-01-01T00:00:00Z') });
    api(async () => json(session(10_000)));
    mount();
    await act(async () => {});
    expect(
      screen.getByRole('button', { name: 'User menu for Actual Administrator' }),
    ).toBeInTheDocument();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_001);
    });
    expect(screen.getByRole('heading', { name: 'Please sign in again' })).toBeInTheDocument();
    expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
  });

  it('polls visible sessions without resetting the overview and detects removed privileges', async () => {
    vi.useFakeTimers();
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible');
    let forbidden = false;
    const fetch = api(async () => (forbidden ? json({}, 403) : json(session())));
    mount();
    await act(async () => {});
    expect(
      screen.getByRole('button', { name: 'User menu for Actual Administrator' }),
    ).toBeInTheDocument();
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
    expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
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
      expect(await screen.findByText('Recent <person>')).toBeInTheDocument();
    },
  );

  it('does not restore stale authorization when an older session check completes after a rejected data request', async () => {
    const pendingSession = deferred<Response>();
    const pendingOverview = deferred<Response>();
    let checkingAgain = false;
    const fetch = api(
      () => (checkingAgain ? pendingSession.promise : Promise.resolve(json(session()))),
      () => pendingOverview.promise,
    );
    mount();
    await screen.findByText('Loading overview…');
    checkingAgain = true;
    fireEvent(window, new Event('focus'));
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/session')).toHaveLength(2);
    await act(async () => pendingOverview.resolve(json({}, 401)));
    await screen.findByRole('heading', { name: 'Please sign in again' });
    await act(async () => {
      pendingSession.resolve(json(session()));
    });
    expect(
      screen.queryByRole('button', { name: 'User menu for Actual Administrator' }),
    ).not.toBeInTheDocument();
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
    const avatar = await screen.findByRole('button', { name: 'User menu for Next Administrator' });
    expect(screen.getByRole('banner')).toContainElement(avatar);
    expect(within(screen.getByRole('complementary')).getByText('Next Administrator')).toBeVisible();
    expect(await screen.findByText('No accounts have registered yet.')).toBeInTheDocument();
    expect(screen.queryByText('Recent <person>')).not.toBeInTheDocument();
    expect(fetch.mock.calls.filter(([url]) => url === '/api/admin/overview')).toHaveLength(2);
  });
});
