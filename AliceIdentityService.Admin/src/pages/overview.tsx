import { Link } from 'react-router-dom';
import { useEffect, useState } from 'react';
import { Users, AppWindow, MailCheck, ShieldCheck } from 'lucide-react';
import { StatCard } from '@/components/stat-card';
import { StatusBadge } from '@/components/user-table';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import type { AdminOverview } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';
import { initials } from '@/lib/person';

export function Overview() {
  const { request } = useAdminSession();
  const [data, setData] = useState<AdminOverview | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminOverview>('/api/admin/overview', controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setData(result);
      })
      .catch(() => {
        /* The shared session boundary handles authorization and service failures. */
      });
    return () => controller.abort();
  }, [request]);

  if (!data) return <p role="status">Loading overview…</p>;
  const { counts } = data;
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Overview</h1>
          <p>A clear view of your identity service.</p>
        </div>
        <span className="overview-updated">
          Updated <time dateTime={data.asOf}>{new Date(data.asOf).toLocaleTimeString()}</time>
        </span>
      </div>
      <nav className="stats-grid" aria-label="Overview statistics">
        <StatCard label="Total users" value={counts.totalUsers} icon={Users} to="/users" />
        <StatCard
          label="Connected applications"
          value={counts.connectedApplications}
          icon={AppWindow}
          to="/clients"
        />
        <StatCard
          label="Custom scopes"
          value={counts.customScopes}
          icon={ShieldCheck}
          to="/scopes"
        />
      </nav>
      <div className="overview-grid">
        <section className="panel">
          <div className="panel-heading">
            <div>
              <h2>Recent registrations</h2>
              <p>The five newest accounts, newest first.</p>
            </div>
          </div>
          <Table aria-label="Recent registrations">
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Joined</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.recentRegistrations.map((user) => (
                <TableRow key={user.id}>
                  <TableCell>
                    <Link className="person" to={'/users/' + encodeURIComponent(user.id)}>
                      <span className="avatar" aria-hidden="true">
                        {initials(user.name)}
                      </span>
                      <span>
                        <strong>{user.name}</strong>
                        <small>{user.email}</small>
                      </span>
                    </Link>
                  </TableCell>
                  <TableCell>
                    <StatusBadge status={user.status} />
                  </TableCell>
                  <TableCell>
                    <time dateTime={user.createdAt}>
                      {new Date(user.createdAt).toLocaleDateString()}
                    </time>
                  </TableCell>
                </TableRow>
              ))}
              {data.recentRegistrations.length === 0 && (
                <TableRow>
                  <TableCell colSpan={3} className="empty-state">
                    No accounts have registered yet.
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </section>
        <section className="panel attention-panel">
          <div className="panel-heading">
            <div>
              <h2>Needs attention</h2>
              <p>Accounts to keep an eye on.</p>
            </div>
          </div>
          <Link className="attention-item" to="/users?status=Unconfirmed">
            <span className="attention-icon">
              <MailCheck size={19} />
            </span>
            <div>
              <strong>
                {counts.unconfirmedUsers.toLocaleString()} unconfirmed{' '}
                {counts.unconfirmedUsers === 1 ? 'account' : 'accounts'}
              </strong>
              <p>These users haven't confirmed their email addresses yet.</p>
            </div>
          </Link>
          <Link className="attention-item" to="/users?status=Locked">
            <span className="attention-icon">
              <ShieldCheck size={19} />
            </span>
            <div>
              <strong>
                {counts.lockedUsers.toLocaleString()} locked{' '}
                {counts.lockedUsers === 1 ? 'account' : 'accounts'}
              </strong>
              <p>Accounts whose enabled lockout has not expired.</p>
            </div>
          </Link>
        </section>
      </div>
    </>
  );
}
