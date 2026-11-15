import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Search, RefreshCw } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { UserTable, StatusBadge } from '@/components/user-table';
import {
  AdminApiError,
  type AdminUserDetail,
  type AdminUserPage,
  type UserSort,
} from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';
import { initials } from '@/lib/person';

const statuses = ['All statuses', 'Confirmed', 'Unconfirmed', 'Locked'];
function positiveInteger(value: string | null, fallback: number, maximum: number) {
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= maximum ? number : fallback;
}
function SearchField({ query, onSearch }: { query: string; onSearch: (value: string) => void }) {
  const [input, setInput] = useState({ query, draft: query });
  // Synchronize browser Back/Forward without replacing the focused input element.
  if (input.query !== query) setInput({ query, draft: query });
  const draft = input.draft;
  useEffect(() => {
    if (draft === query) return;
    const timer = window.setTimeout(() => onSearch(draft), 300);
    return () => window.clearTimeout(timer);
  }, [draft, query, onSearch]);
  return (
    <div className="search-field">
      <Search size={16} aria-hidden="true" />
      <Input
        aria-label="Search users"
        placeholder="Search by name or email…"
        maxLength={256}
        value={draft}
        onChange={(event) => setInput({ query, draft: event.target.value })}
      />
    </div>
  );
}

export function UsersPage() {
  const { request } = useAdminSession();
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const query = params.get('q') ?? '';
  const status = statuses.includes(params.get('status') ?? '')
    ? params.get('status')!
    : 'All statuses';
  const sort = (
    ['name', 'status', 'joined'].includes(params.get('sort') ?? '') ? params.get('sort') : 'name'
  ) as UserSort;
  const direction = params.get('direction') === 'desc' ? 'desc' : 'asc';
  const page = positiveInteger(params.get('page'), 1, 10000);
  const pageSize = positiveInteger(params.get('pageSize'), 25, 100);
  const error =
    query.length > 256
      ? 'Use at most 256 characters.'
      : new Set(query.trim().split(/\s+/u).filter(Boolean)).size > 8
        ? 'Use at most eight search terms.'
        : null;
  const [revision, setRevision] = useState(0);
  const apiParams = new URLSearchParams({
    q: query.trim(),
    sort,
    direction,
    page: String(page),
    pageSize: String(pageSize),
  });
  if (status !== 'All statuses') apiParams.set('status', status);
  const path = '/api/admin/users?' + apiParams;
  const key = path + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminUserPage } | null>(null);
  const data = !error && result?.key === key ? result.data : null;
  useEffect(() => {
    if (error) return;
    const controller = new AbortController();
    request<AdminUserPage>(path, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setResult({ key, data });
      })
      .catch(() => {
        /* The session boundary handles authorization and service failures. */
      });
    return () => controller.abort();
  }, [request, path, key, error]);

  const update = useCallback(
    (changes: Record<string, string | null>, replace = false) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current);
          next.delete('page');
          for (const [key, value] of Object.entries(changes)) {
            if (value) next.set(key, value);
            else next.delete(key);
          }
          return next;
        },
        { replace },
      );
    },
    [setParams],
  );
  const search = useCallback(
    (value: string) => update({ q: value.trim() || null }, true),
    [update],
  );
  const pages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;
  const sizes = [...new Set([25, 50, 100, pageSize])].sort((a, b) => a - b).map(String);
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Users</h1>
          <p>Find people and review their account details.</p>
        </div>
        <Button
          variant="outline"
          disabled={!data}
          onClick={() => setRevision((value) => value + 1)}
        >
          <RefreshCw size={15} />
          Refresh
        </Button>
      </div>
      <section className="panel">
        <div className="table-toolbar">
          <SearchField query={query} onSearch={search} />
          <Select
            items={statuses.map((value) => ({ label: value, value }))}
            value={status}
            onValueChange={(value) =>
              update({ status: value && value !== 'All statuses' ? value : null })
            }
          >
            <SelectTrigger aria-label="Account status">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {statuses.map((value) => (
                <SelectItem key={value} value={value}>
                  {value}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        {error ? (
          <p className="empty-state" role="alert">
            {error}
          </p>
        ) : !data ? (
          <p className="empty-state" role="status">
            Loading users…
          </p>
        ) : (
          <>
            <UserTable
              data={data.items}
              sort={sort}
              direction={direction}
              listSearch={location.search}
              onSort={(value) =>
                update({
                  sort: value,
                  direction: sort === value && direction === 'asc' ? 'desc' : 'asc',
                })
              }
            />
            <div className="table-footer user-pagination">
              <span role="status">
                {data.totalCount === 0
                  ? '0 users'
                  : `${(data.page - 1) * data.pageSize + 1}–${Math.min(data.page * data.pageSize, data.totalCount)} of ${data.totalCount.toLocaleString()} ${data.totalCount === 1 ? 'user' : 'users'}`}
              </span>
              <div className="pagination-controls">
                <Select
                  items={sizes.map((value) => ({ label: `${value} per page`, value }))}
                  value={String(pageSize)}
                  onValueChange={(value) => {
                    if (value) update({ pageSize: value });
                  }}
                >
                  <SelectTrigger aria-label="Users per page">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {sizes.map((value) => (
                      <SelectItem key={value} value={value}>
                        {value} per page
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={data.page <= 1}
                  onClick={() => update({ page: String(data.page - 1) })}
                >
                  Previous
                </Button>
                <span>
                  Page {data.page} of {pages.toLocaleString()}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={data.page >= pages || data.page >= 10000}
                  onClick={() => update({ page: String(data.page + 1) })}
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

export function UserDetail() {
  const { id = '' } = useParams();
  const { search } = useLocation();
  const { request } = useAdminSession();
  const [revision, setRevision] = useState(0);
  const key = id + ':' + revision;
  const [result, setResult] = useState<{ key: string; data: AdminUserDetail | null } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminUserDetail>('/api/admin/users/' + encodeURIComponent(id), controller.signal, {
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
      <Link className="back-link" to={'/users' + search}>
        <ArrowLeft size={15} />
        All users
      </Link>
      {data === undefined ? (
        <p role="status">Loading user…</p>
      ) : data === null ? (
        <>
          <h1>User not found</h1>
          <p>This account may have been removed.</p>
        </>
      ) : (
        <>
          <div className="page-heading">
            <div className="person">
              <span className="avatar large" aria-hidden="true">
                {initials(data.user.name)}
              </span>
              <div>
                <h1>{data.user.name}</h1>
                <p>{data.user.email || 'No email address'}</p>
              </div>
            </div>
            <div className="user-detail-actions">
              <StatusBadge status={data.user.status} />
              <Button variant="outline" onClick={() => setRevision((value) => value + 1)}>
                <RefreshCw size={15} />
                Refresh
              </Button>
            </div>
          </div>
          <Tabs defaultValue="profile">
            <TabsList aria-label="User information">
              <TabsTrigger value="profile">Profile</TabsTrigger>
              <TabsTrigger value="claims">Claims</TabsTrigger>
              <TabsTrigger value="security">Security</TabsTrigger>
            </TabsList>
            <TabsContent value="profile">
              <section className="panel detail-panel">
                <h2>Account details</h2>
                <dl className="detail-list">
                  <div>
                    <dt>User ID</dt>
                    <dd>{data.user.id}</dd>
                  </div>
                  <div>
                    <dt>First name</dt>
                    <dd>{data.firstName}</dd>
                  </div>
                  <div>
                    <dt>Last name</dt>
                    <dd>{data.lastName}</dd>
                  </div>
                  <div>
                    <dt>Display name</dt>
                    <dd>{data.screenName || data.user.name}</dd>
                  </div>
                  <div>
                    <dt>Email address</dt>
                    <dd>{data.user.email || 'No email address'}</dd>
                  </div>
                  <div>
                    <dt>Email confirmed</dt>
                    <dd>{data.emailConfirmed ? 'Yes' : 'No'}</dd>
                  </div>
                  <div>
                    <dt>Account created</dt>
                    <dd>
                      <time dateTime={data.user.createdAt}>
                        {new Date(data.user.createdAt).toLocaleString()}
                      </time>
                    </dd>
                  </div>
                  <div>
                    <dt>Administrator</dt>
                    <dd>{data.user.administrator ? 'Yes' : 'No'}</dd>
                  </div>
                </dl>
                <p>Account editing will be available in Step 9.</p>
              </section>
            </TabsContent>
            <TabsContent value="claims">
              <section className="panel detail-panel">
                <h2>Claims</h2>
                {data.claims.length === 0 ? (
                  <p>No additional claims.</p>
                ) : (
                  <dl className="detail-list">
                    {data.claims.map((claim, index) => (
                      <div key={index}>
                        <dt>{claim.type ?? '(No type)'}</dt>
                        <dd>{claim.value ?? '(No value)'}</dd>
                      </div>
                    ))}
                  </dl>
                )}
                <p>Claim management will be available in Step 10.</p>
              </section>
            </TabsContent>
            <TabsContent value="security">
              <section className="panel detail-panel">
                <h2>Account security</h2>
                <dl className="detail-list">
                  <div>
                    <dt>Status</dt>
                    <dd>{data.user.status}</dd>
                  </div>
                  <div>
                    <dt>Password set</dt>
                    <dd>{data.security.hasPassword ? 'Yes' : 'No'}</dd>
                  </div>
                  <div>
                    <dt>Two-factor enabled</dt>
                    <dd>{data.security.twoFactorEnabled ? 'Yes' : 'No'}</dd>
                  </div>
                  <div>
                    <dt>Lockout enabled</dt>
                    <dd>{data.security.lockoutEnabled ? 'Yes' : 'No'}</dd>
                  </div>
                  <div>
                    <dt>Lockout end</dt>
                    <dd>
                      {data.security.lockoutEnd
                        ? new Date(data.security.lockoutEnd).toLocaleString()
                        : 'None'}
                    </dd>
                  </div>
                  <div>
                    <dt>Failed sign-in attempts</dt>
                    <dd>{data.security.accessFailedCount}</dd>
                  </div>
                </dl>
                <p>Password and lockout actions will be available in Step 9.</p>
              </section>
            </TabsContent>
          </Tabs>
        </>
      )}
    </>
  );
}
