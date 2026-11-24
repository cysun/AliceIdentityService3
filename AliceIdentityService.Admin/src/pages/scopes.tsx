import { useEffect, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Search, ShieldCheck } from 'lucide-react';
import { AdminMutationDialog } from '@/components/admin-mutation-dialog';
import { DirectoryCard, IdentifierList } from '@/components/directory-card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import type { AdminScope, AdminScopePage } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

function scopeTitle(scope: AdminScope) {
  return scope.displayName || scope.name || 'Unnamed scope';
}

function ScopeEditor({
  data,
  onSuccess,
  onReload,
}: {
  data?: AdminScope;
  onSuccess: () => void;
  onReload: () => void;
}) {
  const target = data ? data.name || scopeTitle(data) : undefined;
  const lines = (fields: FormData, name: string) =>
    String(fields.get(name) ?? '')
      .split(/\r?\n/)
      .map((value) => value.trim())
      .filter(Boolean);
  return (
    <AdminMutationDialog
      title={data ? 'Edit scope' : 'Create scope'}
      triggerContent={data ? 'Edit' : undefined}
      triggerLabel={data ? `Edit scope ${target}` : undefined}
      triggerVariant={data ? 'outline' : 'default'}
      submitLabel={data ? 'Save changes' : undefined}
      reloadLabel="Reload scopes"
      description="Define the claims applications may request and the API resources that receive them. Changes apply to newly issued tokens and userinfo responses; issued JWTs retain their original claims until expiry."
      path={'/api/admin/scopes' + (data ? '/' + encodeURIComponent(data.id) : '')}
      target={target}
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

type RefreshScopes = (notice?: string) => void;

function ScopeCard({ scope, refresh }: { scope: AdminScope; refresh: RefreshScopes }) {
  const title = scopeTitle(scope);
  const target = scope.name || title;
  return (
    <DirectoryCard
      title={title}
      identifier={scope.name}
      identifierLabel="Scope name"
      icon={ShieldCheck}
      badge={scope.standard ? 'standard' : undefined}
      description={scope.description}
      actions={
        !scope.standard && (
          <>
            <ScopeEditor
              data={scope}
              onSuccess={() => refresh('Scope updated.')}
              onReload={refresh}
            />
            <AdminMutationDialog
              title="Delete scope"
              triggerContent="Delete"
              triggerLabel={`Delete scope ${target}`}
              description="Permanently remove this definition. Scopes allowed by a registered client or used by an active authorization cannot be deleted."
              target={target}
              confirmation={target}
              path={'/api/admin/scopes/' + encodeURIComponent(scope.id)}
              method="DELETE"
              reloadLabel="Reload scopes"
              input={(fields) => ({
                version: scope.version,
                // The API uses the internal ID for legacy scopes that have no name.
                confirmation: scope.name ? fields.get('confirmation') : (scope.name ?? scope.id),
              })}
              onSuccess={() => refresh('Scope deleted.')}
              onReload={refresh}
            />
          </>
        )
      }
    >
      <div>
        <dt>Claims</dt>
        <dd>
          <IdentifierList values={scope.claims} />
        </dd>
      </div>
      {scope.resources.length > 0 && (
        <div>
          <dt>Resources</dt>
          <dd>
            <IdentifierList values={scope.resources} />
          </dd>
        </div>
      )}
    </DirectoryCard>
  );
}

export function ScopesPage() {
  const { request } = useAdminSession();
  const [params, setParams] = useSearchParams();
  const [revision, setRevision] = useState(0);
  const [notice, setNotice] = useState('');
  const query = (params.get('q') ?? '').slice(0, 200);
  const rawPage = Number(params.get('page') ?? 1);
  const page = Number.isInteger(rawPage) && rawPage > 0 && rawPage <= 10000 ? rawPage : 1;
  const search = new URLSearchParams({ q: query, page: String(page) }).toString();
  const [result, setResult] = useState<{
    search: string;
    revision: number;
    data: AdminScopePage;
  } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminScopePage>('/api/admin/scopes?' + search, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setResult({ search, revision, data });
      })
      .catch(() => {});
    return () => controller.abort();
  }, [search, revision, request]);
  const data = result?.search === search ? result.data : undefined;
  const scopes = data?.items.toSorted((a, b) => Number(a.standard) - Number(b.standard)) ?? [];
  const refreshing = result !== null && result.revision !== revision;
  const pages = data ? Math.ceil(data.totalCount / data.pageSize) : 0;
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const q = String(new FormData(event.currentTarget).get('q') ?? '').trim();
    setNotice('');
    setParams(q ? { q } : {});
  }
  function refresh(message = '') {
    setNotice(message);
    setRevision((value) => value + 1);
  }
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Scopes</h1>
          <p>Define the information applications may request.</p>
        </div>
        <ScopeEditor onSuccess={() => refresh('Scope created.')} onReload={refresh} />
      </div>
      {notice && (
        <p className="operation-notice mb-4" role="status">
          {notice}
        </p>
      )}
      <section className="directory" aria-label="Available scopes">
        <div className="directory-toolbar">
          <form className="scope-search" role="search" onSubmit={submit}>
            <div className="search-field">
              <Search size={16} aria-hidden="true" />
              <Input
                key={query}
                type="search"
                name="q"
                aria-label="Search scopes"
                defaultValue={query}
                placeholder="Search by scope name…"
                maxLength={200}
              />
            </div>
            <Button type="submit" variant="outline">
              Search
            </Button>
          </form>
          <span role="status">
            {data
              ? `${data.totalCount} ${data.totalCount === 1 ? 'scope' : 'scopes'}`
              : 'Loading scopes…'}
          </span>
        </div>
        {data && (
          <div aria-busy={refreshing} inert={refreshing}>
            {scopes.length ? (
              <ul className="directory-grid scope-grid" role="list" aria-label="Scopes">
                {scopes.map((scope) => (
                  <li key={scope.id}>
                    <ScopeCard scope={scope} refresh={refresh} />
                  </li>
                ))}
              </ul>
            ) : (
              <p className="panel panel-note">
                {query ? 'No scopes match this search.' : 'No scopes defined.'}
              </p>
            )}
            {pages > 1 && (
              <div className="table-footer user-pagination">
                <span>
                  Page {data.page} of {pages}
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
                    disabled={data.page >= pages}
                    onClick={() => setParams({ q: query, page: String(data.page + 1) })}
                  >
                    Next
                  </Button>
                </div>
              </div>
            )}
          </div>
        )}
      </section>
    </>
  );
}
