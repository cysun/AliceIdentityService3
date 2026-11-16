import { Link } from 'react-router-dom';
import { useCallback, useEffect, useState } from 'react';
import { Users, AppWindow, MailCheck, ShieldCheck, RefreshCw } from 'lucide-react';
import { StatusBadge } from '@/components/user-table';
import { Button } from '@/components/ui/button';
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
  const [loading, setLoading] = useState(true);
  const [revision, setRevision] = useState(0);
  const refresh = useCallback(() => {
    setLoading(true);
    setRevision((value) => value + 1);
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    request<AdminOverview>('/api/admin/overview', controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setData(result);
      })
      .catch(() => {
        /* The shared session boundary handles authorization and service failures. */
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [request, revision]);

  if (!data) return <p role="status">Loading overview…</p>;
  const { counts } = data;
  const stats = [
    {
      label: 'Total users',
      value: counts.totalUsers,
      note: 'Across your connected applications',
      icon: Users,
    },
    {
      label: 'Connected applications',
      value: counts.connectedApplications,
      note: 'Registered sign-in clients',
      icon: AppWindow,
    },
    {
      label: 'Unconfirmed accounts',
      value: counts.unconfirmedUsers,
      note: 'Awaiting email confirmation',
      icon: MailCheck,
    },
    {
      label: 'Administrators',
      value: counts.administrators,
      note: 'Accounts assigned administrator access',
      icon: ShieldCheck,
    },
  ];
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>Overview</h1>
          <p>A clear view of your identity service.</p>
        </div>
        <div className="overview-refresh">
          <span>
            Updated <time dateTime={data.asOf}>{new Date(data.asOf).toLocaleTimeString()}</time>
          </span>
          <Button variant="outline" size="sm" onClick={refresh} disabled={loading}>
            <RefreshCw size={14} /> {loading ? 'Refreshing…' : 'Refresh'}
          </Button>
        </div>
      </div>
      <div className="stats-grid" aria-busy={loading}>
        {stats.map((stat) => (
          <section className="stat-card" key={stat.label} aria-label={stat.label}>
            <div>
              <span>{stat.label}</span>
              <stat.icon size={18} />
            </div>
            <strong>{stat.value.toLocaleString()}</strong>
            <small>{stat.note}</small>
          </section>
        ))}
      </div>
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
      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Connected applications</h2>
            <p>
              Showing {data.applications.length} of {counts.connectedApplications.toLocaleString()}{' '}
              registered applications.
            </p>
          </div>
        </div>
        {data.applications.length === 0 ? (
          <p className="empty-state">No applications are registered yet.</p>
        ) : (
          <div className="application-grid">
            {data.applications.map((app) => (
              <Link
                className="application-card"
                key={app.id}
                to={'/clients/' + encodeURIComponent(app.id)}
              >
                <span className="avatar" aria-hidden="true">
                  {initials(app.name)}
                </span>
                <div>
                  <strong>{app.name}</strong>
                  <small>{app.clientId}</small>
                </div>
              </Link>
            ))}
          </div>
        )}
      </section>
    </>
  );
}
