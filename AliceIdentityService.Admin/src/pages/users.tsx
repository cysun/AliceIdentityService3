import { useCallback, useEffect, useState } from 'react';
import {
  Link,
  useLocation,
  useNavigate,
  useNavigationType,
  useParams,
  useSearchParams,
} from 'react-router-dom';
import { ArrowLeft, LockKeyhole, MailCheck, Search, ShieldCheck, Users } from 'lucide-react';
import { StatCard } from '@/components/stat-card';
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
  type AdminUserCounts,
  type AdminUserDetail,
  type AdminUserPage,
  type UserSort,
} from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';
import { initials } from '@/lib/person';
import { UserClaims } from './user-claims';
import { UserOperationButton } from './user-operations';

const statuses = ['All statuses', 'Confirmed', 'Unconfirmed', 'Locked'];
function positiveInteger(value: string | null, fallback: number, maximum: number) {
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= maximum ? number : fallback;
}
function SearchField({ query, onSearch }: { query: string; onSearch: (value: string) => void }) {
  const { key } = useLocation();
  const navigation = useNavigationType();
  const [input, setInput] = useState({ key, draft: query, editing: false });
  if (input.key !== key) {
    // Keep typing through a pending navigation. Back and Forward restore the saved search.
    const editing = input.editing && navigation !== 'POP';
    setInput({ key, draft: editing ? input.draft : query, editing });
  }
  const draft = input.draft;
  useEffect(() => {
    if (draft.trim() === query.trim()) return;
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
        onChange={(event) => setInput({ key, draft: event.target.value, editing: true })}
        onBlur={() => setInput((current) => ({ ...current, editing: false }))}
      />
    </div>
  );
}

export function UsersPage() {
  const navigate = useNavigate();
  const { request } = useAdminSession();
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const query = params.get('q') ?? '';
  const status = statuses.includes(params.get('status') ?? '')
    ? params.get('status')!
    : 'All statuses';
  const administratorsOnly = params.get('administratorsOnly') === 'true';
  const showResults = Boolean(query.trim()) || administratorsOnly || status !== 'All statuses';
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
  const [counts, setCounts] = useState<AdminUserCounts | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminUserCounts>('/api/admin/user-counts', controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setCounts(data);
      })
      .catch(() => {
        /* The session boundary handles authorization and service failures. */
      });
    return () => controller.abort();
  }, [request]);
  const apiParams = new URLSearchParams({
    q: query.trim(),
    sort,
    direction,
    page: String(page),
    pageSize: String(pageSize),
  });
  if (status !== 'All statuses') apiParams.set('status', status);
  if (administratorsOnly) apiParams.set('administratorsOnly', 'true');
  const path = '/api/admin/users?' + apiParams;
  const [result, setResult] = useState<{ path: string; data: AdminUserPage } | null>(null);
  const data = showResults && !error && result?.path === path ? result.data : null;
  useEffect(() => {
    if (!showResults || error) return;
    const controller = new AbortController();
    request<AdminUserPage>(path, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setResult({ path, data });
      })
      .catch(() => {
        /* The session boundary handles authorization and service failures. */
      });
    return () => controller.abort();
  }, [request, path, error, showResults]);

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
      <div className="page-heading user-directory-heading">
        <h1>Users</h1>
        <UserOperationButton
          operation="create"
          onSuccess={(id) => navigate('/users/' + encodeURIComponent(id))}
        />
      </div>
      <section className="stats-grid user-stats" aria-label="User statistics" aria-busy={!counts}>
        <StatCard label="Total" value={counts?.totalUsers} icon={Users} />
        <StatCard
          label="Administrators"
          value={counts?.administrators}
          icon={ShieldCheck}
          to="/users?administratorsOnly=true"
          selected={administratorsOnly}
        />
        <StatCard
          label="Unconfirmed"
          value={counts?.unconfirmedUsers}
          icon={MailCheck}
          to="/users?status=Unconfirmed"
          selected={status === 'Unconfirmed'}
        />
        <StatCard
          label="Locked"
          value={counts?.lockedUsers}
          icon={LockKeyhole}
          to="/users?status=Locked"
          selected={status === 'Locked'}
        />
      </section>
      <section className="panel">
        <div className="table-toolbar user-directory-toolbar">
          <SearchField query={query} onSearch={search} />
          {(showResults || error) && (
            <div className="user-detail-actions">
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
              <Link className="clear-user-filters" to="/users">
                Clear
              </Link>
            </div>
          )}
        </div>
        {error && (
          <p className="empty-state" role="alert">
            {error}
          </p>
        )}
        {showResults && !error && !data && (
          <p className="empty-state" role="status">
            Loading users…
          </p>
        )}
        {data && (
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
  const navigate = useNavigate();
  const [tab, setTab] = useState('profile');
  const [notice, setNotice] = useState('');
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
        Back to users
      </Link>
      {notice && (
        <p role="status" className="operation-notice">
          {notice}
        </p>
      )}
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
            <StatusBadge status={data.user.status} />
          </div>
          <Tabs value={tab} onValueChange={setTab}>
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
                <UserOperationButton
                  operation="edit"
                  data={data}
                  onReload={() => setRevision((value) => value + 1)}
                  onSuccess={() => {
                    setNotice('Account updated.');
                    setRevision((value) => value + 1);
                  }}
                />
              </section>
            </TabsContent>
            <TabsContent value="claims">
              <UserClaims
                data={data}
                onReload={() => setRevision((value) => value + 1)}
                onSuccess={() => {
                  setNotice('Claims updated.');
                  setRevision((value) => value + 1);
                }}
              />
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
                      {data.security.lockoutEnd?.startsWith('9999-')
                        ? 'Until unlocked'
                        : data.security.lockoutEnd
                          ? new Date(data.security.lockoutEnd).toLocaleString()
                          : 'None'}
                    </dd>
                  </div>
                  <div>
                    <dt>Failed sign-in attempts</dt>
                    <dd>{data.security.accessFailedCount}</dd>
                  </div>
                </dl>
                <div className="security-actions">
                  {(['password', 'lock', 'unlock', 'delete'] as const).map((operation) => (
                    <UserOperationButton
                      key={operation}
                      operation={operation}
                      data={data}
                      onReload={() => setRevision((value) => value + 1)}
                      onSuccess={() => {
                        if (operation === 'delete') navigate('/users' + search);
                        else {
                          setNotice('Account security updated.');
                          setRevision((value) => value + 1);
                        }
                      }}
                    />
                  ))}
                </div>
              </section>
            </TabsContent>
          </Tabs>
        </>
      )}
    </>
  );
}
