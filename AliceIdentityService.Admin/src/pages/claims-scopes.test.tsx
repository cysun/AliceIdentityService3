import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { AdminShell } from '@/App';
import { AdminAccess } from '@/lib/admin-session';
import type { AdminScope, AdminUserDetail } from '@/lib/admin-api';

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
function api(
  options: {
    self?: boolean;
    standard?: boolean;
    otherScopes?: AdminScope[];
    write?: (body: Record<string, unknown>) => Promise<Response>;
  } = {},
) {
  let user: AdminUserDetail = {
    version: 'A'.repeat(64),
    user: {
      id: options.self ? 'admin' : 'target',
      name: 'Test User',
      email: 'test@example.test',
      status: 'Confirmed',
      createdAt: '2026-11-15T12:00:00Z',
      administrator: !!options.self,
    },
    firstName: 'Test',
    lastName: 'User',
    screenName: 'Test User',
    emailConfirmed: true,
    security: {
      hasPassword: true,
      twoFactorEnabled: false,
      lockoutEnabled: true,
      lockoutEnd: null,
      accessFailedCount: 0,
    },
    claims: [
      { id: 7, type: options.self ? 'ais_admin' : 'department', value: '<script>test</script>' },
    ],
  };
  const scope: AdminScope = {
    id: 'scope-id',
    name: options.standard ? 'profile' : 'courses',
    displayName: 'Courses',
    description: 'Course access',
    claims: ['department'],
    resources: ['courses-api'],
    standard: !!options.standard,
    version: 'A'.repeat(64),
  };
  const scopes = new Map([...(options.otherScopes ?? []), scope].map((item) => [item.id, item]));
  const fetch = vi.fn(async (path: string, init?: RequestInit) => {
    const url = new URL(path, 'https://localhost');
    if (url.pathname === '/api/admin/session')
      return json({
        user: {
          id: 'admin',
          screenName: 'Review Admin',
          firstName: 'Review',
          lastName: 'Admin',
          email: 'admin@example.test',
        },
        serverTime: '2026-11-15T12:00:00Z',
        expiresAt: '2026-11-15T20:00:00Z',
        application: { version: '3.0.0-preview.10', implementationStep: 10 },
      });
    if (url.pathname === '/api/admin/antiforgery')
      return json({ userId: 'admin', requestToken: 'fresh-csrf' });
    if (init?.method === 'POST' || init?.method === 'DELETE') {
      const body = JSON.parse(init.body as string) as Record<string, unknown>;
      if (options.write) return options.write(body);
      if (path.includes('/users/')) {
        user = {
          ...user,
          version: 'B'.repeat(64),
          claims:
            init.method === 'DELETE'
              ? user.claims.filter((claim) => !path.endsWith('/' + claim.id))
              : [...user.claims, { id: 8, type: String(body.type), value: String(body.value) }],
        };
        return json({ id: user.user.id, sessionEnded: !!options.self });
      }
      const creating = url.pathname === '/api/admin/scopes';
      const id = creating
        ? 'created-scope-id'
        : decodeURIComponent(url.pathname.slice('/api/admin/scopes/'.length));
      const current = creating ? scope : scopes.get(id);
      if (!current) return json({}, 404);
      if (init.method === 'DELETE') scopes.delete(id);
      else scopes.set(id, { ...current, ...body, id, version: 'B'.repeat(64) } as AdminScope);
      return json({ id, sessionEnded: false });
    }
    if (url.pathname.startsWith('/api/admin/users/')) return json(user);
    if (url.pathname === '/api/admin/scopes') {
      const query = (url.searchParams.get('q') ?? '').toLowerCase();
      const items = [...scopes.values()].filter((item) =>
        `${item.name ?? ''} ${item.displayName ?? ''}`.toLowerCase().includes(query),
      );
      return json({
        items,
        totalCount: items.length,
        page: 1,
        pageSize: 25,
      });
    }
    throw new Error('Unexpected request: ' + path);
  });
  vi.stubGlobal('fetch', fetch);
  return {
    fetch,
    writes: () =>
      fetch.mock.calls.filter(([, init]) => init?.method === 'POST' || init?.method === 'DELETE'),
  };
}
function CurrentUrl() {
  const { pathname, search } = useLocation();
  return (
    <output hidden data-testid="url">
      {pathname + search}
    </output>
  );
}
function mount(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <CurrentUrl />
      <AdminAccess>
        <AdminShell />
      </AdminAccess>
    </MemoryRouter>,
  );
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('claim assignment', () => {
  it('adds and removes the selected row with CSRF, version and escaped claim text', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/users/target');
    expect(await screen.findByText('<script>test</script>')).toBeVisible();
    expect(document.querySelector('main script')).toBeNull();
    const addClaim = screen.getByRole('button', { name: 'Add claim' });
    expect(addClaim).toHaveClass('border-border', 'bg-background');
    await user.click(addClaim);
    let dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Claim type'), 'role');
    await user.type(within(dialog).getByLabelText('Claim value'), 'faculty');
    const submitClaim = within(dialog).getByRole('button', { name: 'Add claim' });
    expect(submitClaim).toHaveClass('bg-primary');
    await user.click(submitClaim);
    expect(await screen.findByText('faculty')).toBeVisible();
    const [path, request] = mock.writes()[0];
    expect(path).toBe('/api/admin/users/target/claims');
    expect(JSON.parse(request!.body as string)).toEqual({
      version: 'A'.repeat(64),
      type: 'role',
      value: 'faculty',
    });
    expect(request!.headers).toMatchObject({ 'X-CSRF-TOKEN': 'fresh-csrf' });
    const row = screen.getByText('faculty').closest('tr')!;
    const removeClaim = within(row).getByRole('button', { name: 'Remove claim' });
    expect(removeClaim).toHaveClass('text-destructive');
    await user.click(removeClaim);
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/Test User.*role = faculty/)).toBeVisible();
    await user.click(within(dialog).getByRole('button', { name: 'Remove claim' }));
    await waitFor(() => expect(screen.queryByText('faculty')).not.toBeInTheDocument());
    expect(mock.writes()[1][0]).toBe('/api/admin/users/target/claims/8');
    expect(mock.writes()[1][1]!.method).toBe('DELETE');
  });

  it('keeps validation in the dialog and requires reload after a stale write', async () => {
    const mock = api({ write: async () => json({ title: 'This account has changed.' }, 409) });
    const user = userEvent.setup();
    mount('/users/target');
    await user.click(await screen.findByRole('button', { name: 'Add claim' }));
    const dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Claim type'), 'role');
    await user.click(within(dialog).getByRole('button', { name: 'Add claim' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This account has changed.');
    expect(within(dialog).getByRole('button', { name: 'Add claim' })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Reload details' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(mock.writes()).toHaveLength(1);
  });

  it('protects the own last admin claim and ends the session after another own change', async () => {
    api({ self: true });
    const user = userEvent.setup();
    mount('/users/admin');
    expect(await screen.findByRole('button', { name: 'Remove claim' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Add claim' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/You will need to sign in again/)).toBeVisible();
    await user.type(within(dialog).getByLabelText('Claim type'), 'role');
    await user.click(within(dialog).getByRole('button', { name: 'Add claim' }));
    expect(await screen.findByRole('heading', { name: 'Please sign in again' })).toBeVisible();
  });
});

describe('scope definitions', () => {
  it('shows scope definitions on the cards and omits empty resources and read-only actions', async () => {
    api({
      otherScopes: [
        {
          id: 'standard-id',
          name: 'profile',
          displayName: null,
          description: 'Public profile information.',
          claims: ['name'],
          resources: [],
          standard: true,
          version: 'A'.repeat(64),
        },
      ],
    });
    const user = userEvent.setup();
    mount('/scopes?q=o');
    const list = await screen.findByRole('list', { name: 'Scopes' });
    const card = within(list).getByRole('article', { name: 'Courses' });
    const profile = within(list).getByRole('article', { name: 'profile' });
    expect(within(list).getAllByRole('article')).toEqual([card, profile]);
    expect(screen.queryByRole('heading', { name: /^(Custom|Standard) scopes$/ })).toBeNull();
    expect(within(card).queryByText('standard')).toBeNull();
    expect(within(card).getByText('Course access')).toBeVisible();
    expect(within(card).getByText('department')).toBeVisible();
    expect(within(card).getByText('Resources')).toBeVisible();
    expect(within(card).getByText('courses-api')).toBeVisible();
    expect(within(card).getByRole('button', { name: 'Edit scope courses' })).toHaveTextContent(
      'Edit',
    );
    expect(within(card).getByRole('button', { name: 'Delete scope courses' })).toHaveTextContent(
      'Delete',
    );
    const heading = within(profile).getByRole('heading', { level: 2, name: 'profile standard' });
    expect(within(heading).getByText('standard')).toHaveAttribute('data-slot', 'badge');
    expect(within(profile).getByText('Public profile information.')).toBeVisible();
    expect(within(profile).getByText('name')).toBeVisible();
    expect(within(profile).queryByText('Resources')).toBeNull();
    expect(within(profile).queryByRole('button')).toBeNull();
    expect(within(list).queryByRole('link')).toBeNull();
    expect(card).not.toHaveTextContent('scope-id');
    expect(screen.queryByRole('table')).toBeNull();
    const search = await screen.findByRole('searchbox', { name: 'Search scopes' });
    expect(search).toHaveValue('o');
    await user.clear(search);
    await user.type(search, 'profi{Enter}');
    await screen.findByText('1 scope');
    expect(screen.queryByRole('article', { name: 'Courses' })).toBeNull();
    expect(screen.getByRole('heading', { name: 'profile standard' })).toBeVisible();
  });

  it('creates a scope and saves claims and resources with a stable name and current version', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/scopes');
    await user.click(await screen.findByRole('button', { name: 'Create scope' }));
    let dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Scope name'), 'new-scope');
    await user.type(within(dialog).getByLabelText('Display name'), 'New scope');
    await user.type(within(dialog).getByLabelText('Claim types'), 'department\nrole');
    await user.type(within(dialog).getByLabelText('Resources'), 'api-one\napi-two');
    await user.click(within(dialog).getByRole('button', { name: 'Create scope' }));
    const created = await screen.findByRole('article', { name: 'New scope' });
    expect(screen.getByTestId('url')).toHaveTextContent(/^\/scopes$/);
    expect(screen.getByText('Scope created.')).toBeVisible();
    expect(JSON.parse(mock.writes()[0][1]!.body as string)).toMatchObject({
      name: 'new-scope',
      claims: ['department', 'role'],
      resources: ['api-one', 'api-two'],
    });
    await user.click(within(created).getByRole('button', { name: 'Edit scope new-scope' }));
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('Scope name')).toHaveAttribute('readonly');
    await user.clear(within(dialog).getByLabelText('Claim types'));
    await user.type(within(dialog).getByLabelText('Claim types'), 'role');
    await user.type(within(dialog).getByLabelText('Description'), 'Updated scope description.');
    await user.clear(within(dialog).getByLabelText('Resources'));
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Scope updated.')).toBeVisible();
    const updated = await screen.findByRole('article', { name: 'New scope' });
    expect(await within(updated).findByText('Updated scope description.')).toBeVisible();
    expect(within(updated).queryByText('Resources')).toBeNull();
    expect(mock.writes()[1][0]).toBe('/api/admin/scopes/created-scope-id');
    expect(JSON.parse(mock.writes()[1][1]!.body as string)).toMatchObject({
      version: 'B'.repeat(64),
      claims: ['role'],
      resources: [],
    });
  });

  it('requires exact deletion confirmation and supports cancellation without writing', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/scopes?q=courses');
    const card = await screen.findByRole('article', { name: 'Courses' });
    const remove = within(card).getByRole('button', { name: 'Delete scope courses' });
    await user.click(remove);
    let dialog = screen.getByRole('dialog');
    expect(within(dialog).getByRole('button', { name: 'Delete scope' })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(mock.writes()).toHaveLength(0);
    await user.click(remove);
    dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Type courses to confirm'), 'courses');
    await user.click(within(dialog).getByRole('button', { name: 'Delete scope' }));
    expect(await screen.findByText('No scopes match this search.')).toBeVisible();
    expect(screen.getByText('Scope deleted.')).toBeVisible();
    expect(screen.getByRole('searchbox', { name: 'Search scopes' })).toHaveValue('courses');
    expect(mock.writes()[0][0]).toBe('/api/admin/scopes/scope-id');
    expect(mock.writes()[0][1]!.method).toBe('DELETE');
    expect(JSON.parse(mock.writes()[0][1]!.body as string)).toEqual({
      version: 'A'.repeat(64),
      confirmation: 'courses',
    });
  });

  it('redirects old scope links to the directory with the existing query and page', async () => {
    const mock = api({ standard: true });
    mount('/scopes/scope-id?q=profile&page=1');
    expect(await screen.findByText('standard')).toBeVisible();
    expect(screen.getByTestId('url')).toHaveTextContent(/^\/scopes\?q=profile&page=1$/);
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Scopes',
    );
    expect(mock.fetch.mock.calls.map(([path]) => path)).not.toContain('/api/admin/scopes/scope-id');
  });

  it('keeps an unnamed scope’s internal ID out of its card and confirmation prompt', async () => {
    const mock = api({
      otherScopes: [
        {
          id: 'legacy-internal-id',
          name: null,
          displayName: null,
          description: null,
          claims: [],
          resources: [],
          standard: false,
          version: 'A'.repeat(64),
        },
      ],
    });
    const user = userEvent.setup();
    mount('/scopes');
    const card = await screen.findByRole('article', { name: 'Unnamed scope' });
    expect(card).not.toHaveTextContent('legacy-internal-id');
    await user.click(within(card).getByRole('button', { name: 'Delete scope Unnamed scope' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete scope' });
    expect(dialog).not.toHaveTextContent('legacy-internal-id');
    await user.type(
      within(dialog).getByLabelText('Type Unnamed scope to confirm'),
      'Unnamed scope',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Delete scope' }));
    await waitFor(() =>
      expect(screen.queryByRole('article', { name: 'Unnamed scope' })).toBeNull(),
    );
    expect(JSON.parse(mock.writes()[0][1]!.body as string).confirmation).toBe('legacy-internal-id');
  });

  it('reloads a stale card from the current scope search before another edit', async () => {
    const mock = api({ write: async () => json({ title: 'This scope has changed.' }, 409) });
    const user = userEvent.setup();
    mount('/scopes?q=courses');
    await user.click(await screen.findByRole('button', { name: 'Edit scope courses' }));
    const dialog = screen.getByRole('dialog', { name: 'Edit scope' });
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This scope has changed.');
    expect(within(dialog).getByRole('button', { name: 'Save changes' })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Reload scopes' }));
    await waitFor(() =>
      expect(
        mock.fetch.mock.calls.filter(([path]) => path === '/api/admin/scopes?q=courses&page=1'),
      ).toHaveLength(2),
    );
    expect(screen.getByRole('article', { name: 'Courses' })).toBeVisible();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(mock.writes()).toHaveLength(1);
  });

  it('keeps in-use errors visible and never repeats a pending submission', async () => {
    let finish!: (response: Response) => void;
    const mock = api({
      write: () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    });
    const user = userEvent.setup();
    mount('/scopes');
    await user.click(await screen.findByRole('button', { name: 'Delete scope courses' }));
    const dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Type courses to confirm'), 'courses');
    await user.click(within(dialog).getByRole('button', { name: 'Delete scope' }));
    fireEvent.submit(dialog.querySelector('form')!);
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(mock.writes()).toHaveLength(1);
    finish(json({ title: 'A registered client allows this scope.' }, 400));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A registered client allows this scope.',
    );
    expect(within(dialog).getByRole('button', { name: 'Delete scope' })).toBeEnabled();
  });
});
