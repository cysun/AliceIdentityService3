import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
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
  let scope: AdminScope = {
    id: 'scope-id',
    name: options.standard ? 'profile' : 'courses',
    displayName: 'Courses',
    description: 'Course access',
    claims: ['department'],
    resources: ['courses-api'],
    standard: !!options.standard,
    version: 'A'.repeat(64),
  };
  let exists = true;
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
      if (init.method === 'DELETE') exists = false;
      else scope = { ...scope, ...body, version: 'B'.repeat(64) } as AdminScope;
      return json({ id: scope.id, sessionEnded: false });
    }
    if (url.pathname.startsWith('/api/admin/users/')) return json(user);
    if (url.pathname === '/api/admin/scopes')
      return json({
        items: exists ? [scope] : [],
        totalCount: exists ? 1 : 0,
        page: 1,
        pageSize: 25,
      });
    if (url.pathname.startsWith('/api/admin/scopes/')) return json(scope);
    throw new Error('Unexpected request: ' + path);
  });
  vi.stubGlobal('fetch', fetch);
  return {
    fetch,
    writes: () =>
      fetch.mock.calls.filter(([, init]) => init?.method === 'POST' || init?.method === 'DELETE'),
  };
}
function mount(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
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
    await user.click(await screen.findByRole('tab', { name: 'Claims' }));
    expect(await screen.findByText('<script>test</script>')).toBeVisible();
    expect(document.querySelector('main script')).toBeNull();
    const addClaim = screen.getByRole('button', { name: 'Add claim' });
    expect(addClaim).toHaveClass('bg-primary');
    await user.click(addClaim);
    let dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Claim type'), 'role');
    await user.type(within(dialog).getByLabelText('Claim value'), 'faculty');
    await user.click(within(dialog).getByRole('button', { name: 'Add claim' }));
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
    await user.click(await screen.findByRole('tab', { name: 'Claims' }));
    await user.click(screen.getByRole('button', { name: 'Add claim' }));
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
    await user.click(await screen.findByRole('tab', { name: 'Claims' }));
    expect(screen.getByRole('button', { name: 'Remove claim' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Add claim' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/You will need to sign in again/)).toBeVisible();
    await user.type(within(dialog).getByLabelText('Claim type'), 'role');
    await user.click(within(dialog).getByRole('button', { name: 'Add claim' }));
    expect(await screen.findByRole('heading', { name: 'Please sign in again' })).toBeVisible();
  });
});

describe('scope definitions', () => {
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
    expect(await screen.findByRole('heading', { name: 'New scope' })).toBeVisible();
    expect(JSON.parse(mock.writes()[0][1]!.body as string)).toMatchObject({
      name: 'new-scope',
      claims: ['department', 'role'],
      resources: ['api-one', 'api-two'],
    });
    await user.click(screen.getByRole('button', { name: 'Edit scope' }));
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('Scope name')).toHaveAttribute('readonly');
    await user.clear(within(dialog).getByLabelText('Claim types'));
    await user.type(within(dialog).getByLabelText('Claim types'), 'role');
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Scope updated.')).toBeVisible();
    expect(JSON.parse(mock.writes()[1][1]!.body as string)).toMatchObject({
      version: 'B'.repeat(64),
      claims: ['role'],
    });
  });

  it('requires exact deletion confirmation and supports cancellation without writing', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/scopes/scope-id');
    await user.click(await screen.findByRole('button', { name: 'Delete scope' }));
    let dialog = screen.getByRole('dialog');
    expect(within(dialog).getByRole('button', { name: 'Delete scope' })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(mock.writes()).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: 'Delete scope' }));
    dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Type courses to confirm'), 'courses');
    await user.click(within(dialog).getByRole('button', { name: 'Delete scope' }));
    expect(await screen.findByRole('heading', { name: 'Scopes' })).toBeVisible();
    expect(mock.writes()[0][1]!.method).toBe('DELETE');
  });

  it('keeps standard scopes read-only', async () => {
    api({ standard: true });
    mount('/scopes/scope-id');
    expect(await screen.findByText(/Standard scope. Its name/)).toBeVisible();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Scopes',
    );
    expect(screen.queryByRole('button', { name: 'Edit scope' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete scope' })).not.toBeInTheDocument();
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
    mount('/scopes/scope-id');
    await user.click(await screen.findByRole('button', { name: 'Delete scope' }));
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
