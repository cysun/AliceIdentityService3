import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router-dom';
import { AdminShell } from '@/App';
import { AdminAccess } from '@/lib/admin-session';
import type { AdminClientDetail } from '@/lib/admin-api';

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
function api(
  options: {
    confidential?: boolean;
    unsupported?: boolean;
    actor?: string;
    write?: (body: Record<string, unknown>) => Promise<Response>;
  } = {},
) {
  let detail: AdminClientDetail = {
    client: {
      id: 'record',
      clientId: 'campus',
      displayName: 'Campus <Portal>',
      clientType: options.confidential ? 'confidential' : 'public',
      hasSecret: !!options.confidential,
      requirePkce: true,
      scopes: ['email', 'profile'],
    },
    applicationType: 'web',
    consentType: 'implicit',
    redirectUris: ['https://campus.example/callback'],
    postLogoutRedirectUris: ['https://campus.example/logout'],
    allowRefreshTokens: true,
    canManage: !options.unsupported,
    version: 'A'.repeat(64),
  };
  let deleted = false;
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
        application: { version: '3.0.0-preview.11', implementationStep: 11 },
      });
    if (url.pathname === '/api/admin/antiforgery')
      return json({ userId: options.actor ?? 'admin', requestToken: 'fresh-csrf' });
    if (init?.method === 'POST' || init?.method === 'DELETE') {
      const body = JSON.parse(init.body as string) as Record<string, unknown>;
      if (options.write) return options.write(body);
      if (init.method === 'DELETE') deleted = true;
      else if (path.endsWith('/credentials'))
        detail = {
          ...detail,
          version: 'B'.repeat(64),
          client: {
            ...detail.client,
            clientType: String(body.clientType),
            hasSecret: body.clientType === 'confidential',
          },
        };
      else
        detail = {
          ...detail,
          version: 'B'.repeat(64),
          redirectUris: body.redirectUris as string[],
          postLogoutRedirectUris: body.postLogoutRedirectUris as string[],
          allowRefreshTokens: Boolean(body.allowRefreshTokens),
          client: {
            ...detail.client,
            clientId: String(body.clientId),
            displayName: String(body.displayName),
            scopes: body.scopes as string[],
            requirePkce: Boolean(body.requirePkce),
          },
        };
      return json({ id: 'record', sessionEnded: false });
    }
    if (url.pathname === '/api/admin/clients')
      return json({
        items: deleted ? [] : [detail.client],
        totalCount: deleted ? 0 : 1,
        page: 1,
        pageSize: 25,
      });
    if (url.pathname.startsWith('/api/admin/clients/'))
      return deleted ? json({}, 404) : json(detail);
    throw new Error('Unexpected request: ' + path);
  });
  vi.stubGlobal('fetch', fetch);
  return {
    fetch,
    writes: () =>
      fetch.mock.calls.filter(([, init]) => init?.method === 'POST' || init?.method === 'DELETE'),
  };
}
function mount(path = '/clients/record') {
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

async function selectConfidential(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('combobox', { name: 'Client type' }));
  await user.click(
    await screen.findByRole('option', { name: 'Confidential — server application' }),
  );
}

describe('client management', () => {
  it('loads real directory rows, searches on the server and opens escaped details', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/clients');
    expect(await screen.findByRole('link', { name: 'Campus <Portal>' })).toBeVisible();
    await user.type(screen.getByRole('textbox', { name: 'Search clients' }), '%_Campus');
    await user.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() =>
      expect(
        mock.fetch.mock.calls.some(
          ([path]) => new URL(path, 'https://localhost').searchParams.get('q') === '%_Campus',
        ),
      ).toBe(true),
    );
    await user.click(await screen.findByRole('link', { name: 'Campus <Portal>' }));
    expect(await screen.findByRole('heading', { name: 'Campus <Portal>' })).toBeVisible();
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toHaveTextContent(
      'Administration/Clients',
    );
    expect(document.querySelector('main script')).toBeNull();
  });

  it('registers a public PKCE client with the correct flags, scopes and CSRF', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/clients');
    await user.click(await screen.findByRole('button', { name: 'Register client' }));
    const dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Client ID'), 'new-client');
    await user.type(within(dialog).getByLabelText('Display name'), 'New Client');
    await user.type(
      within(dialog).getByLabelText('Redirect URLs', { exact: true }),
      'https://new.example/callback',
    );
    expect(within(dialog).queryByLabelText('New client secret')).not.toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Register client' }));
    expect(await screen.findByRole('heading', { name: 'New Client' })).toBeVisible();
    const [path, request] = mock.writes()[0];
    expect(path).toBe('/api/admin/clients');
    expect(request!.headers).toMatchObject({ 'X-CSRF-TOKEN': 'fresh-csrf' });
    expect(JSON.parse(request!.body as string)).toMatchObject({
      clientId: 'new-client',
      clientType: 'public',
      clientSecret: null,
      redirectUris: ['https://new.example/callback'],
      postLogoutRedirectUris: [],
      scopes: ['profile', 'email'],
      allowRefreshTokens: true,
      requirePkce: true,
    });
  });

  it('generates secrets locally, masks them by default and clears them when the dialog closes', async () => {
    const mock = api();
    const user = userEvent.setup();
    mount('/clients');
    await user.click(await screen.findByRole('button', { name: 'Register client' }));
    await selectConfidential(user);
    let dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Generate secret' }));
    const field = within(dialog).getByLabelText('New client secret') as HTMLInputElement;
    const secret = field.value;
    expect(secret).toMatch(/^[A-Za-z0-9_-]{43}$/);
    expect(field).toHaveAttribute('type', 'password');
    await user.click(within(dialog).getByRole('button', { name: 'Show secret' }));
    expect(field).toHaveAttribute('type', 'text');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getByRole('button', { name: 'Register client' }));
    await selectConfidential(user);
    dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('New client secret')).toHaveValue('');
    expect(mock.writes()).toHaveLength(0);
    expect(JSON.stringify(mock.fetch.mock.calls)).not.toContain(secret);
  });

  it('edits settings without sending a secret or changing the client ID', async () => {
    const mock = api({ confidential: true });
    const user = userEvent.setup();
    mount();
    await user.click(await screen.findByRole('button', { name: 'Edit client' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('Client ID')).toHaveAttribute('readonly');
    expect(within(dialog).queryByLabelText('New client secret')).not.toBeInTheDocument();
    await user.clear(within(dialog).getByLabelText('Display name'));
    await user.type(within(dialog).getByLabelText('Display name'), 'Edited Portal');
    await user.click(within(dialog).getByRole('checkbox', { name: 'Allow refresh tokens' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByRole('heading', { name: 'Edited Portal' })).toBeVisible();
    const body = JSON.parse(mock.writes()[0][1]!.body as string);
    expect(body).toMatchObject({
      clientId: 'campus',
      version: 'A'.repeat(64),
      allowRefreshTokens: false,
    });
    expect(body).not.toHaveProperty('clientSecret');
    expect(body).not.toHaveProperty('clientType');
  });

  it('requires exact confirmation for credential changes and does not reuse the existing secret', async () => {
    const mock = api({ confidential: true });
    const user = userEvent.setup();
    mount();
    expect(await screen.findByText('Configured (not retrievable)')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Change credentials' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByLabelText('New client secret')).toHaveValue('');
    expect(within(dialog).getByRole('button', { name: 'Save credentials' })).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Type campus to confirm'), 'campus');
    await user.click(within(dialog).getByRole('button', { name: 'Generate secret' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save credentials' }));
    expect(await screen.findByText('Client updated.')).toBeVisible();
    expect(mock.writes()[0][0]).toBe('/api/admin/clients/record/credentials');
    expect(JSON.parse(mock.writes()[0][1]!.body as string)).toMatchObject({
      version: 'A'.repeat(64),
      clientType: 'confidential',
      confirmation: 'campus',
    });
    expect(screen.queryByLabelText('New client secret')).not.toBeInTheDocument();
  });

  it('keeps validation in the form, requires reload for stale edits and cancels without writing', async () => {
    const mock = api({ write: async () => json({ title: 'This client has changed.' }, 409) });
    const user = userEvent.setup();
    mount();
    await user.click(await screen.findByRole('button', { name: 'Edit client' }));
    let dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(mock.writes()).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: 'Edit client' }));
    dialog = screen.getByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Save changes' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('This client has changed.');
    expect(within(dialog).getByRole('button', { name: 'Save changes' })).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Reload details' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(mock.writes()).toHaveLength(1);
  });

  it('requires delete confirmation and prevents duplicate pending writes', async () => {
    let finish!: (response: Response) => void;
    const mock = api({
      write: () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    });
    const user = userEvent.setup();
    mount();
    await user.click(await screen.findByRole('button', { name: 'Delete client' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByRole('button', { name: 'Delete client' })).toBeDisabled();
    await user.type(within(dialog).getByLabelText('Type campus to confirm'), 'campus');
    await user.click(within(dialog).getByRole('button', { name: 'Delete client' }));
    fireEvent.submit(dialog.querySelector('form')!);
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(mock.writes()).toHaveLength(1);
    finish(json({ id: 'record', sessionEnded: false }));
    expect(await screen.findByRole('heading', { name: 'Clients' })).toBeVisible();
  });

  it('clears credential forms on actor changes without sending a write', async () => {
    const mock = api({ confidential: true, actor: 'different-admin' });
    const user = userEvent.setup();
    mount();
    await user.click(await screen.findByRole('button', { name: 'Change credentials' }));
    const dialog = screen.getByRole('dialog');
    await user.type(within(dialog).getByLabelText('Type campus to confirm'), 'campus');
    await user.click(within(dialog).getByRole('button', { name: 'Generate secret' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save credentials' }));
    expect(await screen.findByRole('heading', { name: 'Please sign in again' })).toBeVisible();
    expect(mock.writes()).toHaveLength(0);
    expect(screen.queryByLabelText('New client secret')).not.toBeInTheDocument();
  });

  it('keeps unsupported imported settings read-only', async () => {
    api({ unsupported: true });
    mount();
    expect(await screen.findByText(/This imported client uses a type/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Edit client' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change credentials' })).not.toBeInTheDocument();
  });
});
