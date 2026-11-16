import { useEffect, useState, type FormEvent } from 'react';
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, RefreshCw } from 'lucide-react';
import { AdminMutationDialog } from '@/components/admin-mutation-dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { AdminApiError, type AdminScope, type AdminScopePage } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

function ScopeEditor({
  data,
  onSuccess,
  onReload,
}: {
  data?: AdminScope;
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
      title={data ? 'Edit scope' : 'Create scope'}
      submitLabel={data ? 'Save changes' : undefined}
      description="Define the claims applications may request and the API resources that receive them. Changes apply to newly issued tokens and userinfo responses; issued JWTs retain their original claims until expiry."
      path={'/api/admin/scopes' + (data ? '/' + encodeURIComponent(data.id) : '')}
      target={data?.name ?? undefined}
      input={(fields) => ({
        ...(data ? { version: data.version } : {}),
        name: fields.get('name'),
        displayName: fields.get('displayName'),
        description: fields.get('description'),
        claims: lines(fields, 'claims'),
        resources: lines(fields, 'resources'),
      })}
      onSuccess={onSuccess}
      onReload={onReload}
    >
      <Label>
        Scope name
        <Input
          name="name"
          defaultValue={data?.name ?? ''}
          readOnly={!!data}
          required
          maxLength={200}
          autoComplete="off"
        />
      </Label>
      <p className="form-hint">
        Names are case-sensitive and stay fixed after creation. Client permissions are managed
        separately.
      </p>
      <Label>
        Display name
        <Input
          name="displayName"
          defaultValue={data?.displayName ?? ''}
          maxLength={200}
          autoComplete="off"
        />
      </Label>
      <Label>
        Description
        <textarea
          name="description"
          defaultValue={data?.description ?? ''}
          rows={3}
          maxLength={2000}
        />
      </Label>
      <Label>
        Claim types
        <textarea
          name="claims"
          defaultValue={data?.claims.join('\n') ?? ''}
          rows={4}
          maxLength={25700}
        />
      </Label>
      <p className="form-hint">
        One type per line, up to 100. A scope exposes matching existing user claims; it does not
        assign them. Private security and protocol claims cannot be exposed.
      </p>
      <Label>
        Resources
        <textarea
          name="resources"
          defaultValue={data?.resources.join('\n') ?? ''}
          rows={3}
          maxLength={32000}
        />
      </Label>
      <p className="form-hint">
        One API resource identifier per line, up to 100. These become access-token audiences.
      </p>
    </AdminMutationDialog>
  );
}

export function ScopesPage() {
  const { request } = useAdminSession();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const [revision, setRevision] = useState(0);
  const query = (params.get('q') ?? '').slice(0, 200);
  const rawPage = Number(params.get('page') ?? 1);
  const page = Number.isInteger(rawPage) && rawPage > 0 && rawPage <= 10000 ? rawPage : 1;
  const search = new URLSearchParams({ q: query, page: String(page) }).toString();
  const key = search + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminScopePage } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminScopePage>('/api/admin/scopes?' + search, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setResult({ key, data });
      })
      .catch(() => {});
    return () => controller.abort();
  }, [key, search, request]);
  const data = result?.key === key ? result.data : undefined;
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const q = String(new FormData(event.currentTarget).get('q') ?? '').trim();
    setParams(q ? { q } : {});
  }
  const reload = () => setRevision((value) => value + 1);
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Scopes</h1>
          <p>Define the information applications may request.</p>
        </div>
        <ScopeEditor
          onSuccess={(id) => navigate('/scopes/' + encodeURIComponent(id))}
          onReload={reload}
        />
      </div>
      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Available scopes</h2>
            <p>Standard scopes preserve the existing identity contract.</p>
          </div>
          <Button variant="outline" onClick={reload}>
            <RefreshCw size={15} />
            Refresh
          </Button>
        </div>
        <form className="table-toolbar" onSubmit={submit}>
          <Input
            key={query}
            name="q"
            aria-label="Search scopes"
            defaultValue={query}
            placeholder="Search by scope name…"
            maxLength={200}
          />
          <Button type="submit" variant="outline">
            Search
          </Button>
        </form>
        {!data ? (
          <p className="detail-panel" role="status">
            Loading scopes…
          </p>
        ) : (
          <>
            <Table className="claim-table">
              <TableHeader>
                <TableRow>
                  <TableHead>Scope</TableHead>
                  <TableHead>Claims</TableHead>
                  <TableHead>Resources</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {data.items.map((scope) => (
                  <TableRow key={scope.id}>
                    <TableCell>
                      <Link to={'/scopes/' + encodeURIComponent(scope.id) + '?' + search}>
                        {scope.displayName || scope.name || scope.id}
                      </Link>
                      <div>
                        <code>{scope.name}</code>{' '}
                        {scope.standard && <Badge variant="secondary">Standard</Badge>}
                      </div>
                    </TableCell>
                    <TableCell>
                      <code>{scope.claims.join(' · ') || 'None'}</code>
                    </TableCell>
                    <TableCell>
                      <code>{scope.resources.join(' · ') || 'None'}</code>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {data.items.length === 0 && (
              <p className="detail-panel">No scopes match this search.</p>
            )}
            <div className="table-footer user-pagination">
              <span>
                {data.totalCount} scopes · Page {data.page} of{' '}
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

export function ScopeDetail() {
  const { id = '' } = useParams();
  const { search } = useLocation();
  const navigate = useNavigate();
  const { request } = useAdminSession();
  const [revision, setRevision] = useState(0);
  const [notice, setNotice] = useState('');
  const key = id + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminScope | null } | null>(null);
  const reload = () => setRevision((value) => value + 1);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminScope>('/api/admin/scopes/' + encodeURIComponent(id), controller.signal, {
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
      <Link className="back-link" to={'/scopes' + search}>
        <ArrowLeft size={15} />
        All scopes
      </Link>
      {notice && (
        <p role="status" className="operation-notice">
          {notice}
        </p>
      )}
      {data === undefined ? (
        <p role="status">Loading scope…</p>
      ) : data === null ? (
        <h1>Scope not found</h1>
      ) : (
        <>
          <div className="page-heading">
            <div>
              <h1>{data.displayName || data.name || data.id}</h1>
              <p>{data.name}</p>
            </div>
            {!data.standard && (
              <ScopeEditor
                data={data}
                onSuccess={() => {
                  setNotice('Scope updated.');
                  reload();
                }}
                onReload={reload}
              />
            )}
          </div>
          <section className="panel detail-panel">
            <h2>Scope definition</h2>
            {data.standard && (
              <p>Standard scope. Its name and claim mapping are managed by the service.</p>
            )}
            <dl className="detail-list">
              <div>
                <dt>Description</dt>
                <dd>{data.description || 'None'}</dd>
              </div>
              <div>
                <dt>Scope ID</dt>
                <dd>{data.id}</dd>
              </div>
              <div>
                <dt>Claims</dt>
                <dd>
                  {data.claims.length
                    ? data.claims.map((claim) => (
                        <div key={claim}>
                          <code>{claim}</code>
                        </div>
                      ))
                    : 'None'}
                </dd>
              </div>
              <div>
                <dt>Resources</dt>
                <dd>
                  {data.resources.length
                    ? data.resources.map((resource) => (
                        <div key={resource}>
                          <code>{resource}</code>
                        </div>
                      ))
                    : 'None'}
                </dd>
              </div>
            </dl>
            {!data.standard && (
              <div className="security-actions">
                <AdminMutationDialog
                  title="Delete scope"
                  description="Permanently remove this definition. Scopes allowed by a registered client or used by an active authorization cannot be deleted."
                  target={data.name ?? data.id}
                  confirmation={data.name ?? data.id}
                  path={'/api/admin/scopes/' + encodeURIComponent(id)}
                  method="DELETE"
                  input={(fields) => ({
                    version: data.version,
                    confirmation: fields.get('confirmation'),
                  })}
                  onSuccess={() => navigate('/scopes' + search)}
                  onReload={reload}
                />
              </div>
            )}
          </section>
        </>
      )}
    </>
  );
}
