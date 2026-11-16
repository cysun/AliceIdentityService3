import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom';
import { AdminShell } from '@/App';
import { AdminAccess } from '@/lib/admin-session';
import type { AdminUser, AdminUserDetail, AdminUserPage } from '@/lib/admin-api';

const person: AdminUser = {
  id: 'actual-person',
  name: 'Actual <person>',
  email: 'person@example.test',
  status: 'Confirmed',
  createdAt: '2026-11-14T12:00:00Z',
  administrator: true,
};
const directory: AdminUserPage = {
  items: [person],
  totalCount: 1,
  page: 1,
  pageSize: 25,
  asOf: '2026-11-15T12:00:00Z',
};
const detail: AdminUserDetail = {
  version: 'A'.repeat(64),
  user: person,
  firstName: 'Actual',
  lastName: 'Person',
  screenName: person.name,
  emailConfirmed: true,
  security: {
    hasPassword: true,
    twoFactorEnabled: false,
    lockoutEnabled: true,
    lockoutEnd: null,
    accessFailedCount: 2,
  },
  claims: [
    { type: 'ais_admin', value: 'false' },
    { type: 'department', value: '<script>example</script>' },
  ],
};
function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
function api(
  users = (_url: URL): Promise<Response> => Promise.resolve(json(directory)),
  user = (): Promise<Response> => Promise.resolve(json(detail)),
) {
  const fetch = vi.fn((path: string) => {
    const url = new URL(path, 'https://localhost');
    if (url.pathname === '/api/admin/session')
      return Promise.resolve(
        json({
          user: {
            id: 'admin',
            screenName: 'Directory Admin',
            firstName: 'Directory',
            lastName: 'Admin',
            email: 'admin@example.test',
          },
          serverTime: '2026-11-15T12:00:00Z',
          expiresAt: '2026-11-15T20:00:00Z',
          application: {
            name: 'Alice Identity Service',
            version: '3.0.0-preview.9',
            implementationStep: 9,
          },
        }),
      );
    if (url.pathname === '/api/admin/users') return users(url);
    if (url.pathname.startsWith('/api/admin/users/')) return user();
    throw new Error('Unexpected API path: ' + path);
  });
  vi.stubGlobal('fetch', fetch);
  return fetch;
}
function History() {
  const navigate = useNavigate();
  const location = useLocation();
  return (
    <>
      <output data-testid="url">{location.pathname + location.search}</output>
      <button onClick={() => navigate(-1)}>History back</button>
      <button onClick={() => navigate(1)}>History forward</button>
    </>
  );
}
function mount(path = '/users') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <History />
      <AdminAccess>
        <AdminShell />
      </AdminAccess>
    </MemoryRouter>,
  );
}
function calls(fetch: ReturnType<typeof api>) {
  return fetch.mock.calls
    .map(([path]) => new URL(path, 'https://localhost'))
    .filter((url) => url.pathname === '/api/admin/users');
}
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('live user directory', () => {
  it('loads bookmarked query/filter/sort/page state from the API with no sample records', async () => {
    const fetch = api(async () => json({ ...directory, page: 2, totalCount: 75 }));
    mount('/users?q=smith+john&status=Unconfirmed&sort=joined&direction=desc&page=2');
    expect(await screen.findByRole('link', { name: /Actual <person>/ })).toBeInTheDocument();
    const request = calls(fetch)[0].searchParams;
    expect(Object.fromEntries(request)).toEqual({
      q: 'smith john',
      status: 'Unconfirmed',
      sort: 'joined',
      direction: 'desc',
      page: '2',
      pageSize: '25',
    });
    expect(screen.getByRole('textbox', { name: 'Search users' })).toHaveValue('smith john');
    expect(screen.getByText('26–50 of 75 users')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Joined' })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    expect(screen.queryByText('Alex Morgan')).not.toBeInTheDocument();
    expect(screen.queryByText('Fictional sample data')).not.toBeInTheDocument();
  });

  it('debounces typing, preserves focus and sends literal search input without client-side filtering', async () => {
    const fetch = api();
    mount();
    await screen.findByRole('table', { name: 'Users' });
    const input = screen.getByRole('textbox', { name: 'Search users' });
    await userEvent.setup().type(input, '%_\\Smith John');
    expect(calls(fetch)).toHaveLength(1);
    await waitFor(() => expect(calls(fetch)).toHaveLength(2));
    expect(calls(fetch)[1].searchParams.get('q')).toBe('%_\\Smith John');
    expect(input).toHaveFocus();
    expect(screen.getByRole('link', { name: /Actual <person>/ })).toBeInTheDocument();
    await userEvent.setup().type(input, 'x');
    await waitFor(() => expect(calls(fetch)).toHaveLength(3));
    expect(calls(fetch)[2].searchParams.get('q')).toBe('%_\\Smith Johnx');
  });

  it('ignores an old response arriving after a newer search and hides previous-query rows while loading', async () => {
    const pending = deferred();
    const fetch = api((url) =>
      url.searchParams.get('q') === 'old' ? pending.promise : Promise.resolve(json(directory)),
    );
    mount();
    await screen.findByRole('table', { name: 'Users' });
    const input = screen.getByRole('textbox', { name: 'Search users' });
    fireEvent.change(input, { target: { value: 'old' } });
    await waitFor(() => expect(calls(fetch)).toHaveLength(2));
    expect(screen.getByText('Loading users…')).toBeInTheDocument();
    expect(screen.queryByText(person.name)).not.toBeInTheDocument();
    fireEvent.change(input, { target: { value: 'new' } });
    await waitFor(() => expect(calls(fetch)).toHaveLength(3));
    await screen.findByRole('table', { name: 'Users' });
    await act(async () =>
      pending.resolve(json({ ...directory, items: [{ ...person, name: 'Stale person' }] })),
    );
    expect(screen.queryByText('Stale person')).not.toBeInTheDocument();
    expect(screen.getByText(person.name)).toBeInTheDocument();
  });

  it('paginates on the server, resets the page for sorting and filters, and restores history', async () => {
    const fetch = api(async (url) =>
      json({
        ...directory,
        totalCount: 80,
        page: Number(url.searchParams.get('page')),
        pageSize: Number(url.searchParams.get('pageSize')),
      }),
    );
    const user = userEvent.setup();
    mount('/users?q=person');
    await screen.findByRole('table', { name: 'Users' });
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('26–50 of 80 users');
    expect(calls(fetch).at(-1)!.searchParams.get('page')).toBe('2');
    await user.click(screen.getByRole('button', { name: 'Joined' }));
    await waitFor(() => expect(calls(fetch).at(-1)!.searchParams.get('sort')).toBe('joined'));
    expect(calls(fetch).at(-1)!.searchParams.get('page')).toBe('1');
    await user.click(screen.getByRole('button', { name: 'History back' }));
    await screen.findByText('26–50 of 80 users');
    expect(screen.getByRole('textbox', { name: 'Search users' })).toHaveValue('person');
    await user.click(screen.getByRole('combobox', { name: 'Account status' }));
    await user.click(await screen.findByRole('option', { name: 'Locked' }));
    await waitFor(() => expect(calls(fetch).at(-1)!.searchParams.get('status')).toBe('Locked'));
    expect(calls(fetch).at(-1)!.searchParams.get('page')).toBe('1');
    expect(calls(fetch).at(-1)!.searchParams.get('q')).toBe('person');
    await screen.findByText('1–25 of 80 users');
    await user.click(screen.getByRole('combobox', { name: 'Users per page' }));
    await user.click(await screen.findByRole('option', { name: '50 per page' }));
    await screen.findByText('1–50 of 80 users');
    expect(calls(fetch).at(-1)!.searchParams.get('pageSize')).toBe('50');
  });

  it('shows empty results and refreshes the current query', async () => {
    let empty = true;
    const fetch = api(async () =>
      json(empty ? { ...directory, items: [], totalCount: 0 } : directory),
    );
    mount('/users?q=example&status=Confirmed');
    await screen.findByText('No users match your filters.');
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    empty = false;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }));
    await screen.findByText(person.name);
    expect(calls(fetch)[0].search).toBe(calls(fetch)[1].search);
  });

  it('validates overly broad input locally and recovers when corrected', async () => {
    const fetch = api();
    mount('/users?q=one+two+three+four+five+six+seven+eight+nine');
    await screen.findByText('Use at most eight search terms.');
    expect(calls(fetch)).toHaveLength(0);
    fireEvent.change(screen.getByRole('textbox', { name: 'Search users' }), {
      target: { value: 'smith' },
    });
    await screen.findByText(person.name);
    expect(calls(fetch)).toHaveLength(1);
  });

  it('normalizes invalid URL controls before requesting data', async () => {
    const fetch = api();
    mount('/users?page=-1&pageSize=1000&sort=PasswordHash&direction=bad&status=bad');
    await screen.findByText(person.name);
    expect(Object.fromEntries(calls(fetch)[0].searchParams)).toEqual({
      q: '',
      sort: 'name',
      direction: 'asc',
      page: '1',
      pageSize: '25',
    });
  });

  it('opens live profile, claims and security and preserves the list URL', async () => {
    api();
    mount('/users?q=actual&status=Confirmed');
    const user = userEvent.setup();
    await user.click(await screen.findByRole('link', { name: /Actual <person>/ }));
    await screen.findByRole('heading', { name: person.name });
    expect(screen.getByText('actual-person')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'All users' })).toHaveAttribute(
      'href',
      '/users?q=actual&status=Confirmed',
    );
    await user.click(screen.getByRole('tab', { name: 'Claims' }));
    expect(screen.getByText('<script>example</script>')).toBeInTheDocument();
    expect(document.querySelector('.detail-panel script')).toBeNull();
    expect(screen.getByText('false')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Security' }));
    expect(screen.getByText('Failed sign-in attempts').nextElementSibling).toHaveTextContent('2');
    expect(
      screen.queryByRole('button', { name: /add claim|assign claim/i }),
    ).not.toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'All users' }));
    await screen.findByRole('table', { name: 'Users' });
    expect(screen.getByRole('textbox', { name: 'Search users' })).toHaveValue('actual');
  });

  it('shows missing users inside the authorized shell and supports return to the directory', async () => {
    api(undefined, async () => json({}, 404));
    mount('/users/missing?q=smith');
    await screen.findByRole('heading', { name: 'User not found' });
    expect(screen.getByRole('navigation', { name: 'Administration' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'All users' })).toHaveAttribute(
      'href',
      '/users?q=smith',
    );
  });

  it('handles no email, no password and empty claims on a deep-linked detail', async () => {
    api(undefined, async () =>
      json({
        ...detail,
        user: { ...person, email: null, administrator: false },
        claims: [],
        security: { ...detail.security, hasPassword: false },
      }),
    );
    mount('/users/actual-person');
    await screen.findByRole('heading', { name: person.name });
    await userEvent.setup().click(screen.getByRole('tab', { name: 'Claims' }));
    expect(screen.getByText('No additional claims.')).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole('tab', { name: 'Security' }));
    expect(screen.getByText('Password set').nextElementSibling).toHaveTextContent('No');
  });

  it.each([401, 403])(
    'clears user data when a subsequent directory request returns %s',
    async (status) => {
      let denied = false;
      api(async () => (denied ? json({}, status) : json(directory)));
      mount();
      await screen.findByText(person.name);
      denied = true;
      await userEvent.setup().click(screen.getByRole('button', { name: 'Refresh' }));
      expect(await screen.findByRole('alert')).toHaveTextContent(
        status === 401 ? 'Please sign in again' : 'Administrator access required',
      );
      expect(screen.queryByText(person.name)).not.toBeInTheDocument();
    },
  );

  it('retries service failures without showing a stale result', async () => {
    let broken = true;
    api(async () => {
      if (broken) throw new TypeError('Offline');
      return json(directory);
    });
    mount();
    await screen.findByRole('heading', { name: 'Administration is temporarily unavailable' });
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    broken = false;
    await userEvent.setup().click(screen.getByRole('button', { name: 'Try again' }));
    await screen.findByText(person.name);
  });
});
