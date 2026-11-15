import { useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Search } from 'lucide-react';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { UserTable, StatusBadge } from '@/components/user-table';
import { users, initials } from '@/preview-data';
const statuses = ['All statuses', 'Confirmed', 'Unconfirmed', 'Locked'];
export function UsersPage() {
  const [query, setQuery] = useState('');
  const [params, setParams] = useSearchParams();
  const status = statuses.includes(params.get('status') ?? '')
    ? params.get('status')!
    : 'All statuses';
  const filtered = useMemo(
    () =>
      users.filter(
        (user) =>
          (status === 'All statuses' || user.status === status) &&
          `${user.name} ${user.email}`.toLowerCase().includes(query.trim().toLowerCase()),
      ),
    [query, status],
  );
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Users</h1>
          <p>Find people and review their account details.</p>
        </div>
        <span className="sample-label">Fictional sample data</span>
      </div>
      <section className="panel">
        <div className="table-toolbar">
          <div className="search-field">
            <Search size={16} aria-hidden="true" />
            <Input
              aria-label="Search users"
              placeholder="Search by name or email…"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
          </div>
          <Select
            items={statuses.map((value) => ({ label: value, value }))}
            value={status}
            onValueChange={(value) =>
              setParams(value && value !== 'All statuses' ? { status: value } : {})
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
        <UserTable data={filtered} />
        <div className="table-footer" role="status">
          {filtered.length} of {users.length} sample users · Changes are not saved
        </div>
      </section>
    </>
  );
}
export function UserDetail() {
  const { id } = useParams();
  const user = users.find((item) => item.id === id);
  if (!user)
    return (
      <>
        <h1>User not found</h1>
        <Link to="/users">Back to users</Link>
      </>
    );
  return (
    <>
      <Link className="back-link" to="/users">
        <ArrowLeft size={15} /> All users
      </Link>
      <div className="page-heading">
        <div className="person">
          <span className="avatar large">{initials(user.name)}</span>
          <div>
            <h1>{user.name}</h1>
            <p>{user.email}</p>
          </div>
        </div>
        <StatusBadge status={user.status} />
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
            <p>Read-only sample for reviewing the layout.</p>
            <dl className="detail-list">
              <div>
                <dt>Display name</dt>
                <dd>{user.name}</dd>
              </div>
              <div>
                <dt>Email address</dt>
                <dd>{user.email}</dd>
              </div>
              <div>
                <dt>Account created</dt>
                <dd>{user.joined}</dd>
              </div>
              <div>
                <dt>Administrator</dt>
                <dd>{user.administrator ? 'Yes' : 'No'}</dd>
              </div>
            </dl>
          </section>
        </TabsContent>
        <TabsContent value="claims">
          <section className="panel detail-panel">
            <h2>Claims</h2>
            <p>
              {user.administrator
                ? 'Sample claim: ais_admin = true'
                : 'No additional claims in this sample.'}
            </p>
            <p>Claim management will be implemented in Step 10.</p>
          </section>
        </TabsContent>
        <TabsContent value="security">
          <section className="panel detail-panel">
            <h2>Account security</h2>
            <p>
              Status: {user.status}. Password and lockout actions will be implemented in Step 9.
            </p>
          </section>
        </TabsContent>
      </Tabs>
    </>
  );
}
