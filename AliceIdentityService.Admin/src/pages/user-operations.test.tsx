import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { AdminShell } from '@/App';
import { AdminAccess } from '@/lib/admin-session';
import type { AdminUserDetail } from '@/lib/admin-api';

const original: AdminUserDetail = {
  version: 'A'.repeat(64),
  user: {
    id: 'target',
    name: 'Test Person',
    email: 'person@example.test',
    status: 'Confirmed',
    createdAt: '2026-11-14T12:00:00Z',
    administrator: false,
  },
  firstName: 'Test',
  lastName: 'Person',
  screenName: 'Test Person',
  emailConfirmed: true,
  security: {
    hasPassword: true,
    twoFactorEnabled: false,
    lockoutEnabled: true,
    lockoutEnd: null,
    accessFailedCount: 2,
  },
  claims: [],
};
function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
function api(
  options: {
    self?: boolean;
    tokenUser?: string;
    write?: (path: string, body: Record<string, unknown>) => Promise<Response | void>;
  } = {},
) {
  let detail = structuredClone(original);
  if (options.self) detail.user.id = 'admin';
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
        application: {
          name: 'Alice Identity Service',
          version: '3.0.0-preview.9',
          implementationStep: 9,
        },
      });
    if (url.pathname === '/api/admin/antiforgery')
      return json({ requestToken: 'fresh-csrf', userId: options.tokenUser ?? 'admin' });
    if (url.pathname === '/api/admin/user-counts')
      return json({ totalUsers: 2, administrators: 1, unconfirmedUsers: 0, lockedUsers: 0 });
    if (init?.method === 'POST' || init?.method === 'DELETE') {
      const body = JSON.parse(init.body as string) as Record<string, unknown>;
      const response = await options.write?.(path, body);
      if (response) return response;
      if (path.endsWith('/profile'))
        detail = { ...detail, firstName: String(body.firstName), version: 'B'.repeat(64) };
      if (path.endsWith('/lock')) {
        detail.user.status = 'Locked';
        detail.security.lockoutEnd = '2026-11-16T12:00:00Z';
        detail.version = 'B'.repeat(64);
      }
      if (path.endsWith('/unlock')) {
        detail.user.status = 'Confirmed';
        detail.security.lockoutEnd = null;
        detail.security.accessFailedCount = 0;
        detail.version = 'C'.repeat(64);
      }
      return json({ id: detail.user.id, sessionEnded: false });
    }
    if (url.pathname === '/api/admin/users')
      return json({
        items: [detail.user],
        totalCount: 1,
        page: 1,
        pageSize: 25,
        asOf: '2026-11-15T12:00:00Z',
      });
    if (url.pathname.startsWith('/api/admin/users/')) return json(detail);
    throw new Error('Unexpected request: ' + path);
  });
  vi.stubGlobal('fetch', fetch);
  return {
    fetch,
    writes: () =>
      fetch.mock.calls.filter(([, init]) => init?.method === 'POST' || init?.method === 'DELETE'),
  };
}
function Location() {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}
function mount(path = '/users/target?q=person') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Location />
      <AdminAccess>
        <AdminShell />
      </AdminAccess>
    </MemoryRouter>,
  );
}
async function open(name: string) {
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name }));
  return within(await screen.findByRole('dialog', { name }));
}
function fill(name: string, value: string) {
  fireEvent.change(screen.getByLabelText(name), { target: { value } });
}
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('administrative account operations', () => {
  it('opens Profile below Scopes and edits the signed-in administrator’s names', async () => {
    const server = api({ self: true });
    const user = userEvent.setup();
    mount('/users/admin');
    await screen.findByRole('heading', { name: 'Test Person' });
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Users',
    );
    const profile = screen.getByRole('link', { name: 'Profile' });
    await user.click(profile);
    await screen.findByRole('heading', { level: 1, name: 'Profile' });
    expect(profile).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Profile',
    );
    expect(await screen.findByRole('link', { name: 'Change password' })).toHaveAttribute(
      'href',
      '/Account/ChangePassword',
    );
    await user.click(screen.getByRole('button', { name: 'Edit profile' }));
    const dialog = within(await screen.findByRole('dialog'));
    expect(dialog.queryByLabelText('Email confirmed')).toBeNull();
    fill('First name', 'Updated');
    await user.click(dialog.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Profile updated.')).toBeVisible();
    expect(await screen.findByText('Updated')).toBeVisible();
    expect(server.writes()[0][0]).toBe('/api/admin/users/admin/profile');
    expect(JSON.parse(server.writes()[0][1]!.body as string)).toMatchObject({
      firstName: 'Updated',
      emailConfirmed: true,
      version: original.version,
    });
  });

  it('cancels without saving and submits only editable profile fields with a fresh CSRF token', async () => {
    const server = api();
    mount();
    let dialog = await open('Edit account');
    expect(dialog.queryByLabelText('Email address')).not.toBeInTheDocument();
    fill('First name', 'Discarded');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Cancel' }));
    expect(server.writes()).toHaveLength(0);
    dialog = await open('Edit account');
    expect(dialog.getByLabelText('First name')).toHaveValue('Test');
    fill('First name', ' Updated ');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Account updated.');
    await screen.findByText('Updated');
    const [path, request] = server.writes()[0];
    expect(path).toBe('/api/admin/users/target/profile');
    expect(JSON.parse(request!.body as string)).toEqual({
      version: original.version,
      firstName: 'Updated',
      lastName: 'Person',
      screenName: 'Test Person',
      emailConfirmed: true,
    });
    expect(request!.headers).toMatchObject({ 'X-CSRF-TOKEN': 'fresh-csrf' });
    expect(request!.credentials).toBe('same-origin');
    expect(
      server.fetch.mock.calls.findIndex(([path]) => path === '/api/admin/antiforgery'),
    ).toBeLessThan(server.fetch.mock.calls.findIndex(([, init]) => init?.method === 'POST'));
    dialog = await open('Edit account');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(server.writes()).toHaveLength(2));
    expect(JSON.parse(server.writes()[1][1]!.body as string).version).toBe('B'.repeat(64));
    expect(
      server.fetch.mock.calls.filter(([path]) => path === '/api/admin/antiforgery'),
    ).toHaveLength(2);
  });

  it('creates an account, reports validation errors in the form, and opens the created detail', async () => {
    let duplicate = true;
    const server = api({
      write: async () =>
        duplicate
          ? json({ title: 'An account already uses this email address.' }, 400)
          : json({ id: 'target', sessionEnded: false }),
    });
    mount('/users');
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Create account' }));
    const dialog = within(await screen.findByRole('dialog'));
    fill('First name', 'New');
    fill('Last name', 'Person');
    fill('Screen name', 'New Person');
    fill('Email address', 'person@example.test');
    fill('New password', 'Valid-Password-42!');
    fill('Confirm new password', 'Different-Password-42!');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Create account' }));
    expect(await dialog.findByRole('alert')).toHaveTextContent('The passwords do not match');
    expect(server.writes()).toHaveLength(0);
    fill('Confirm new password', 'Valid-Password-42!');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Create account' }));
    expect(await dialog.findByRole('alert')).toHaveTextContent('already uses this email');
    expect(dialog.getByLabelText('First name')).toHaveValue('New');
    duplicate = false;
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Create account' }));
    await screen.findByRole('heading', { name: 'Test Person' });
    expect(screen.getByTestId('url')).toHaveTextContent('/users/target');
    expect(screen.getByTestId('url')).not.toHaveTextContent('Password');
  });

  it('requires a reload after a stale edit instead of resubmitting an old version', async () => {
    const server = api({ write: async () => json({ title: 'This account has changed.' }, 409) });
    mount();
    const dialog = await open('Edit account');
    fill('First name', 'Stale');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Save changes' }));
    await dialog.findByRole('alert');
    expect(dialog.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    expect(dialog.getByLabelText('First name')).toBeDisabled();
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Reload account' }));
    await screen.findByRole('heading', { name: 'Test Person' });
    expect(server.writes()).toHaveLength(1);
    expect((await open('Edit account')).getByLabelText('First name')).toHaveValue('Test');
  });

  it('switches between Lock and Unlock after each change and prevents duplicate submissions', async () => {
    let resolve!: () => void;
    const waiting = new Promise<void>((complete) => {
      resolve = complete;
    });
    const server = api({ write: () => waiting });
    const user = userEvent.setup();
    mount();
    expect(await screen.findByRole('button', { name: 'Lock account' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
    const dialog = await open('Lock account');
    await user.dblClick(dialog.getByRole('button', { name: 'Lock account' }));
    expect(server.writes()).toHaveLength(1);
    expect(dialog.getByRole('button', { name: 'Saving…' })).toBeDisabled();
    expect(dialog.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    await user.keyboard('{Escape}');
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    await act(async () => resolve());
    await screen.findByText('Account security updated.');
    expect(await screen.findByRole('button', { name: 'Unlock account' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Lock account' })).not.toBeInTheDocument();
    const unlock = await open('Unlock account');
    await user.click(unlock.getByRole('button', { name: 'Unlock account' }));
    expect(await screen.findByRole('button', { name: 'Lock account' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Unlock account' })).not.toBeInTheDocument();
    expect(server.writes().map(([path]) => path)).toEqual([
      '/api/admin/users/target/lock',
      '/api/admin/users/target/unlock',
    ]);
  });

  it('requires exact deletion confirmation and returns to the preserved directory query', async () => {
    const server = api();
    mount();
    const dialog = await open('Delete account');
    const button = dialog.getByRole('button', { name: 'Delete account' });
    expect(button).toBeDisabled();
    const input = dialog.getByRole('textbox');
    fireEvent.change(input, { target: { value: 'wrong' } });
    expect(button).toBeDisabled();
    fireEvent.change(input, { target: { value: 'person@example.test' } });
    await userEvent.setup().click(button);
    await screen.findByRole('table', { name: 'Users' });
    expect(server.writes()[0][1]!.method).toBe('DELETE');
    expect(JSON.parse(server.writes()[0][1]!.body as string)).toEqual({
      version: original.version,
      confirmation: 'person@example.test',
    });
    expect(screen.getByTestId('url')).toHaveTextContent('/users?q=person');
  });

  it('clears passwords on cancel, protects the current administrator, and signs out after an own-password reset', async () => {
    api({ self: true, write: async () => json({ id: 'admin', sessionEnded: true }) });
    mount('/users/admin');
    let dialog = await open('Edit account');
    expect(dialog.getByRole('checkbox', { name: 'Email confirmed' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByRole('button', { name: 'Delete account' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Lock account' })).toBeDisabled();
    dialog = await open('Reset password');
    fill('New password', 'Secret-Password-42!');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Cancel' }));
    dialog = await open('Reset password');
    expect(dialog.getByLabelText('New password')).toHaveValue('');
    fill('New password', 'Secret-Password-42!');
    fill('Confirm new password', 'Secret-Password-42!');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Reset password' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Please sign in again');
    expect(screen.queryByRole('navigation', { name: 'Administration' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('New password')).not.toBeInTheDocument();
  });

  it('refuses to write if the browser has switched to a different signed-in administrator', async () => {
    const server = api({ tokenUser: 'another-admin' });
    mount();
    const dialog = await open('Edit account');
    await userEvent.setup().click(dialog.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Please sign in again');
    expect(server.writes()).toHaveLength(0);
  });

  it.each([401, 403, 500])(
    'clears protected forms after a %s write failure and never retries the write',
    async (status) => {
      const server = api({ write: async () => json({}, status) });
      mount();
      const dialog = await open('Edit account');
      await userEvent.setup().click(dialog.getByRole('button', { name: 'Save changes' }));
      expect(await screen.findByRole('alert')).toHaveTextContent(
        status === 401
          ? 'Please sign in again'
          : status === 403
            ? 'Administrator access required'
            : 'Administration is temporarily unavailable',
      );
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      if (status === 500) {
        expect(screen.getByRole('alert')).toHaveTextContent('The change may have completed');
        await userEvent.setup().click(screen.getByRole('button', { name: 'Try again' }));
        await screen.findByRole('heading', { name: 'Test Person' });
      }
      expect(server.writes()).toHaveLength(1);
    },
  );
});
