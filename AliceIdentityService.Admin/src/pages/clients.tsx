import { useEffect, useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft } from 'lucide-react';
import { AdminMutationDialog } from '@/components/admin-mutation-dialog';
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
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { AdminApiError, type AdminClientDetail, type AdminClientPage } from '@/lib/admin-api';
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

export function ClientsPage() {
  const { request } = useAdminSession();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const [revision, setRevision] = useState(0);
  const query = (params.get('q') ?? '').slice(0, 200);
  const rawPage = Number(params.get('page') ?? 1);
  const page = Number.isInteger(rawPage) && rawPage > 0 && rawPage <= 10000 ? rawPage : 1;
  const search = new URLSearchParams({ q: query, page: String(page) }).toString();
  const key = search + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminClientPage } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminClientPage>('/api/admin/clients?' + search, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setResult({ key, data });
      })
      .catch(() => {});
    return () => controller.abort();
  }, [key, search, request]);
  const data = result?.key === key ? result.data : undefined;
  const reload = () => setRevision((value) => value + 1);
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const q = String(new FormData(event.currentTarget).get('q') ?? '').trim();
    setParams(q ? { q } : {});
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
      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Registered applications</h2>
            <p>Manage sign-in settings and the information each application may request.</p>
          </div>
        </div>
        <form className="table-toolbar" onSubmit={submit}>
          <Input
            key={query}
            name="q"
            aria-label="Search clients"
            defaultValue={query}
            placeholder="Search by name or client ID…"
            maxLength={200}
          />
          <Button type="submit" variant="outline">
            Search
          </Button>
        </form>
        {!data ? (
          <p className="detail-panel" role="status">
            Loading clients…
          </p>
        ) : (
          <>
            <Table className="claim-table" aria-label="Clients">
              <TableHeader>
                <TableRow>
                  <TableHead>Application</TableHead>
                  <TableHead>Type</TableHead>
                  <TableHead>Allowed scopes</TableHead>
                  <TableHead>PKCE</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.items.map((client) => (
                  <TableRow key={client.id}>
                    <TableCell>
                      <Link to={'/clients/' + encodeURIComponent(client.id) + '?' + search}>
                        {client.displayName || client.clientId || client.id}
                      </Link>
                      <div>
                        <code>{client.clientId}</code>
                      </div>
                    </TableCell>
                    <TableCell>{client.clientType || 'Unknown'}</TableCell>
                    <TableCell>
                      <code>{client.scopes.join(' · ') || 'None'}</code>
                    </TableCell>
                    <TableCell>{client.requirePkce ? 'Required' : 'Optional'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {data.items.length === 0 && (
              <p className="detail-panel">No clients match this search.</p>
            )}
            <div className="table-footer user-pagination">
              <span>
                {data.totalCount} clients · Page {data.page} of{' '}
                {Math.max(1, Math.ceil(data.totalCount / data.pageSize))}
              </span>
              <div className="pagination-controls">
                <Button
                  variant="outline"
                  disabled={data.page <= 1}
                  onClick={() => setParams({ q: query, page: String(data.page - 1) })}
                >
                  Previous
                </Button>
                <Button
                  variant="outline"
                  disabled={data.page * data.pageSize >= data.totalCount}
                  onClick={() => setParams({ q: query, page: String(data.page + 1) })}
                >
                  Next
                </Button>
              </div>
            </div>
          </>
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
              <h1>{data.client.displayName || data.client.clientId || data.client.id}</h1>
              <p>{data.client.clientId}</p>
            </div>
            {data.canManage && <ClientEditor data={data} onSuccess={saved} onReload={reload} />}
          </div>
          <section className="panel detail-panel">
            <h2>Client settings</h2>
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
            <div className="security-actions">
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
          </section>
        </>
      )}
    </>
  );
}
