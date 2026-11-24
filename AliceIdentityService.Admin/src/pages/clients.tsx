import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { AppWindow, ArrowLeft, ListFilter } from 'lucide-react';
import { AdminMutationDialog } from '@/components/admin-mutation-dialog';
import { DirectoryCard, IdentifierList } from '@/components/directory-card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  AdminApiError,
  type AdminClient,
  type AdminClientDetail,
  type AdminClientPage,
} from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

const types = [
  { value: 'public', label: 'Public — browser application' },
  { value: 'confidential', label: 'Confidential — server application' },
];
function CredentialFields({ initialType = 'public' }: { initialType?: string }) {
  const [type, setType] = useState(initialType);
  const [secret, setSecret] = useState('');
  const [show, setShow] = useState(false);
  function generate() {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    setSecret(
      btoa(String.fromCharCode(...bytes))
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=+$/, ''),
    );
  }
  return (
    <>
      <div>
        <Label>Client type</Label>
        <Select
          name="clientType"
          value={type}
          items={types}
          onValueChange={(value) => {
            if (value) {
              setType(value);
              setSecret('');
              setShow(false);
            }
          }}
        >
          <SelectTrigger aria-label="Client type">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {types.map((item) => (
              <SelectItem key={item.value} value={item.value}>
                {item.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {type === 'confidential' ? (
        <>
          <Label>
            New client secret
            <Input
              name="clientSecret"
              type={show ? 'text' : 'password'}
              value={secret}
              onChange={(event) => setSecret(event.target.value)}
              minLength={32}
              maxLength={256}
              required
              autoComplete="new-password"
              spellCheck={false}
            />
          </Label>
          <div className="credential-actions">
            <Button type="button" variant="outline" onClick={generate}>
              Generate secret
            </Button>
            <Button
              type="button"
              variant="outline"
              aria-pressed={show}
              onClick={() => setShow(!show)}
            >
              {show ? 'Hide secret' : 'Show secret'}
            </Button>
          </div>
          <p className="form-hint">
            Save this secret in the application's private configuration before submitting. It cannot
            be retrieved afterward. Use 32–256 characters; Generate creates a random secret.
          </p>
        </>
      ) : (
        <p className="form-hint">
          Public clients cannot keep a secret. PKCE is required; switching to public removes the old
          secret and enables PKCE.
        </p>
      )}
    </>
  );
}
function SettingsFields({ data }: { data?: AdminClientDetail }) {
  return (
    <>
      <Label>
        Client ID
        <Input
          name="clientId"
          defaultValue={data?.client.clientId ?? ''}
          required
          readOnly={!!data}
          maxLength={100}
          autoComplete="off"
        />
      </Label>
      <p className="form-hint">Client IDs are case-sensitive and stay fixed after registration.</p>
      <Label>
        Display name
        <Input
          name="displayName"
          defaultValue={data?.client.displayName ?? ''}
          required
          maxLength={200}
          autoComplete="off"
        />
      </Label>
      {!data && <CredentialFields />}
      <Label>
        Redirect URLs
        <textarea
          name="redirectUris"
          defaultValue={data?.redirectUris.join('\n') ?? ''}
          required
          rows={3}
          maxLength={24000}
        />
      </Label>
      <Label>
        Post-logout redirect URLs
        <textarea
          name="postLogoutRedirectUris"
          defaultValue={data?.postLogoutRedirectUris.join('\n') ?? ''}
          rows={3}
          maxLength={24000}
        />
      </Label>
      <p className="form-hint">
        One exact URL per line. New URLs use HTTPS; HTTP is allowed on localhost or a loopback IP.
        No wildcards, credentials or fragments. Existing imported callbacks may be retained
        unchanged.
      </p>
      <Label>
        Allowed scopes
        <textarea
          name="scopes"
          defaultValue={data?.client.scopes.join('\n') ?? 'profile\nemail'}
          rows={3}
          maxLength={20100}
        />
      </Label>
      <p className="form-hint">
        One registered scope name per line.{' '}
        <a href="/admin/scopes" target="_blank" rel="noreferrer">
          Browse scopes
        </a>
        . openid is always available. offline_access requires refresh tokens.
      </p>
      <label className="confirmation-check">
        <Checkbox name="allowRefreshTokens" defaultChecked={data?.allowRefreshTokens ?? true} />
        Allow refresh tokens
      </label>
      <label className="confirmation-check">
        <Checkbox name="requirePkce" defaultChecked={data?.client.requirePkce ?? true} />
        Require PKCE (S256)
      </label>
      <p className="form-hint">
        Public clients must enable PKCE before saving. New clients use authorization code flow with
        implicit consent.
      </p>
    </>
  );
}
function ClientEditor({
  data,
  onSuccess,
  onReload,
}: {
  data?: AdminClientDetail;
  onSuccess: (id: string) => void;
  onReload: () => void;
}) {
  const lines = (fields: FormData, name: string) =>
    String(fields.get(name) ?? '')
      .split(/\r?\n/)
      .map((value) => value.trim())
      .filter(Boolean);
  return (
    <AdminMutationDialog
      title={data ? 'Edit client' : 'Register client'}
      triggerVariant={data ? 'outline' : 'default'}
      submitLabel={data ? 'Save changes' : undefined}
      description={
        data
          ? 'Update this application. Changes to redirects, allowed scopes, refresh tokens or PKCE invalidate its existing codes and refresh tokens. Display-name-only edits retain grants.'
          : 'Register an application that can sign users in through AIS3. Check the callback URLs and allowed scopes before saving.'
      }
      target={data?.client.clientId ?? undefined}
      path={'/api/admin/clients' + (data ? '/' + encodeURIComponent(data.client.id) : '')}
      input={(fields) => ({
        ...(data
          ? { version: data.version }
          : { clientType: fields.get('clientType'), clientSecret: fields.get('clientSecret') }),
        clientId: fields.get('clientId'),
        displayName: fields.get('displayName'),
        redirectUris: lines(fields, 'redirectUris'),
        postLogoutRedirectUris: lines(fields, 'postLogoutRedirectUris'),
        scopes: lines(fields, 'scopes'),
        allowRefreshTokens: fields.has('allowRefreshTokens'),
        requirePkce: fields.has('requirePkce'),
      })}
      onSuccess={onSuccess}
      onReload={onReload}
    >
      <SettingsFields data={data} />
    </AdminMutationDialog>
  );
}

function clientName(client: AdminClient) {
  return client.displayName || client.clientId || client.id;
}

function ClientCard({ client, search }: { client: AdminClient; search: string }) {
  return (
    <DirectoryCard
      to={{ pathname: '/clients/' + encodeURIComponent(client.id), search }}
      title={clientName(client)}
      identifier={client.clientId}
      identifierLabel="Client ID"
      icon={AppWindow}
    >
      <div>
        <dt>Client type</dt>
        <dd className="first-letter:uppercase">{client.clientType || 'Unknown'}</dd>
      </div>
      <div>
        <dt>PKCE</dt>
        <dd>{client.requirePkce ? 'Required' : 'Optional'}</dd>
      </div>
      <div className="col-span-full">
        <dt>Allowed scopes</dt>
        <dd>
          <IdentifierList values={client.scopes} />
        </dd>
      </div>
    </DirectoryCard>
  );
}

const clientsPerPage = 25;

export function ClientsPage() {
  const { request } = useAdminSession();
  const navigate = useNavigate();
  const { key, search } = useLocation();
  const [params, setParams] = useSearchParams();
  const [revision, setRevision] = useState(0);
  const savedFilter = (params.get('q') ?? '').slice(0, 200);
  const [draft, setDraft] = useState<{ key: string; value: string } | null>(null);
  const filter = draft?.key === key ? draft.value : savedFilter;
  const filterSearch = filter ? '?' + new URLSearchParams({ q: filter }) : '';
  const listSearch = filter === savedFilter ? search : filterSearch;
  const rawPage = Number(params.get('page') ?? 1);
  const [clients, setClients] = useState<AdminClient[] | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    async function load() {
      const records = new Map<string, AdminClient>();
      let page = 1;
      while (true) {
        const data = await request<AdminClientPage>(
          `/api/admin/clients?page=${page}&pageSize=100`,
          controller.signal,
        );
        if (controller.signal.aborted) return;
        for (const client of data.items) records.set(client.id, client);
        if (data.page * data.pageSize >= data.totalCount) break;
        page = data.page + 1;
      }
      setClients([...records.values()]);
    }
    load().catch(() => {
      /* The session boundary handles authorization and service failures. */
    });
    return () => controller.abort();
  }, [revision, request]);
  function reload() {
    setClients(null);
    setRevision((value) => value + 1);
  }
  const term = filter.trim().toLowerCase();
  const matches =
    clients?.filter((client) => clientName(client).toLowerCase().includes(term)) ?? [];
  const pages = Math.max(1, Math.ceil(matches.length / clientsPerPage));
  const page =
    filter === savedFilter && Number.isInteger(rawPage) && rawPage > 0
      ? Math.min(rawPage, pages)
      : 1;
  const visible = matches.slice((page - 1) * clientsPerPage, page * clientsPerPage);
  function goToPage(next: number) {
    setParams(filter ? { q: filter, page: String(next) } : { page: String(next) });
  }
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Clients</h1>
          <p>Applications registered with Alice Identity.</p>
        </div>
        <ClientEditor
          onSuccess={(id) => navigate('/clients/' + encodeURIComponent(id))}
          onReload={reload}
        />
      </div>
      <section className="directory" aria-label="Registered applications">
        <div className="directory-toolbar">
          <div className="search-field">
            <ListFilter size={16} aria-hidden="true" />
            <Input
              type="search"
              aria-label="Filter clients by name"
              placeholder="Filter by name…"
              maxLength={200}
              value={filter}
              onChange={(event) => setDraft({ key, value: event.target.value })}
              onBlur={() => {
                // Save the filter for Back without navigating on every keystroke.
                if (filter !== savedFilter) setParams(filterSearch, { replace: true });
              }}
            />
          </div>
          <span role="status">
            {clients ? (
              <>
                {term && `${matches.length} of `}
                {clients.length} {clients.length === 1 ? 'client' : 'clients'}
              </>
            ) : (
              'Loading clients…'
            )}
          </span>
        </div>
        {clients &&
          (matches.length ? (
            <ul className="directory-grid" role="list" aria-label="Clients">
              {visible.map((client) => (
                <li key={client.id}>
                  <ClientCard client={client} search={listSearch} />
                </li>
              ))}
            </ul>
          ) : (
            <p className="panel panel-note">
              {clients.length ? 'No clients match this name.' : 'No clients registered.'}
            </p>
          ))}
        {pages > 1 && (
          <div className="table-footer user-pagination">
            <span>
              Page {page} of {pages}
            </span>
            <div className="pagination-controls">
              <Button variant="outline" disabled={page <= 1} onClick={() => goToPage(page - 1)}>
                Previous
              </Button>
              <Button variant="outline" disabled={page >= pages} onClick={() => goToPage(page + 1)}>
                Next
              </Button>
            </div>
          </div>
        )}
      </section>
    </>
  );
}

export function ClientDetail() {
  const { id = '' } = useParams();
  const { search } = useLocation();
  const navigate = useNavigate();
  const { request } = useAdminSession();
  const [revision, setRevision] = useState(0);
  const [notice, setNotice] = useState('');
  const key = id + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminClientDetail | null } | null>(
    null,
  );
  const reload = () => setRevision((value) => value + 1);
  const saved = () => {
    setNotice('Client updated.');
    reload();
  };
  useEffect(() => {
    const controller = new AbortController();
    request<AdminClientDetail>('/api/admin/clients/' + encodeURIComponent(id), controller.signal, {
      allowNotFound: true,
    })
      .then((data) => {
        if (!controller.signal.aborted) setResult({ key, data });
      })
      .catch((error) => {
        if (!controller.signal.aborted && error instanceof AdminApiError && error.status === 404)
          setResult({ key, data: null });
      });
    return () => controller.abort();
  }, [id, key, request]);
  const data = result?.key === key ? result.data : undefined;
  return (
    <>
      <Link className="back-link" to={'/clients' + search}>
        <ArrowLeft size={15} />
        All clients
      </Link>
      {notice && (
        <p role="status" className="operation-notice">
          {notice}
        </p>
      )}
      {data === undefined ? (
        <p role="status">Loading client…</p>
      ) : data === null ? (
        <h1>Client not found</h1>
      ) : (
        <>
          <div className="page-heading">
            <div>
              <h1>{clientName(data.client)}</h1>
              <p>{data.client.clientId}</p>
            </div>
            <div className="heading-actions">
              {data.canManage && <ClientEditor data={data} onSuccess={saved} onReload={reload} />}
              <AdminMutationDialog
                title="Delete client"
                description="Permanently remove this registration and its stored authorizations and tokens. The application will no longer be able to sign users in through this client ID. This cannot be undone."
                target={data.client.clientId ?? data.client.id}
                confirmation={data.client.clientId ?? data.client.id}
                path={'/api/admin/clients/' + encodeURIComponent(id)}
                method="DELETE"
                input={(fields) => ({
                  version: data.version,
                  confirmation: fields.get('confirmation'),
                })}
                onSuccess={() => navigate('/clients' + search)}
                onReload={reload}
              />
            </div>
          </div>
          <section className="panel detail-panel">
            <header className="panel-heading">
              <h2>Client settings</h2>
              {data.canManage && (
                <AdminMutationDialog
                  title="Change credentials"
                  submitLabel="Save credentials"
                  description="Rotate the secret or change the client type. This invalidates this application's codes and refresh tokens. Update the application configuration before signing in again. Converting to public removes the secret and enables PKCE."
                  target={data.client.clientId ?? data.client.id}
                  confirmation={data.client.clientId ?? data.client.id}
                  path={'/api/admin/clients/' + encodeURIComponent(id) + '/credentials'}
                  input={(fields) => ({
                    version: data.version,
                    clientType: fields.get('clientType'),
                    clientSecret: fields.get('clientSecret'),
                    confirmation: fields.get('confirmation'),
                  })}
                  onSuccess={saved}
                  onReload={reload}
                >
                  <CredentialFields initialType={data.client.clientType ?? 'public'} />
                </AdminMutationDialog>
              )}
            </header>
            {!data.canManage && (
              <p>
                This imported client uses a type, consent mode or flow outside the supported
                authorization-code configuration. Its settings are read-only.
              </p>
            )}
            {data.client.clientType === 'public' && !data.client.requirePkce && (
              <p>
                This imported public client does not require PKCE. Enable PKCE before saving its
                settings.
              </p>
            )}
            <dl className="detail-list">
              <div>
                <dt>Client ID</dt>
                <dd>{data.client.clientId}</dd>
              </div>
              <div>
                <dt>Type</dt>
                <dd>{data.client.clientType || 'Unknown'}</dd>
              </div>
              <div>
                <dt>Client secret</dt>
                <dd>{data.client.hasSecret ? 'Configured (not retrievable)' : 'None'}</dd>
              </div>
              <div>
                <dt>PKCE</dt>
                <dd>{data.client.requirePkce ? 'Required (S256)' : 'Optional'}</dd>
              </div>
              <div>
                <dt>Refresh tokens</dt>
                <dd>{data.allowRefreshTokens ? 'Allowed' : 'Not allowed'}</dd>
              </div>
              <div>
                <dt>Consent</dt>
                <dd>{data.consentType || 'Not configured'}</dd>
              </div>
              <div>
                <dt>Redirect URLs</dt>
                <dd>
                  {data.redirectUris.map((uri) => (
                    <div key={uri}>
                      <code>{uri}</code>
                    </div>
                  ))}
                </dd>
              </div>
              <div>
                <dt>Post-logout redirect URLs</dt>
                <dd>
                  {data.postLogoutRedirectUris.length
                    ? data.postLogoutRedirectUris.map((uri) => (
                        <div key={uri}>
                          <code>{uri}</code>
                        </div>
                      ))
                    : 'None'}
                </dd>
              </div>
              <div>
                <dt>Allowed scopes</dt>
                <dd>{data.client.scopes.join(' · ') || 'None'}</dd>
              </div>
            </dl>
            <p>
              openid is always available. offline_access requires refresh-token permission. Already
              issued JWTs and application-local sessions may remain valid until their own expiry.
            </p>
          </section>
        </>
      )}
    </>
  );
}
