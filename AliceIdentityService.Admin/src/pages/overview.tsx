import { Link } from 'react-router-dom';
import { ArrowRight, Users, AppWindow, MailCheck, ShieldCheck } from 'lucide-react';
import { UserTable } from '@/components/user-table';
import { users, applications } from '@/preview-data';
export function Overview() {
  const unconfirmed = users.filter((user) => user.status === 'Unconfirmed');
  const stats = [
    {
      label: 'Total users',
      value: users.length,
      note: 'Across your connected applications',
      icon: Users,
    },
    {
      label: 'Connected applications',
      value: applications.length,
      note: 'One identity, shared access',
      icon: AppWindow,
    },
    {
      label: 'Unconfirmed accounts',
      value: unconfirmed.length,
      note: 'Awaiting email confirmation',
      icon: MailCheck,
    },
    {
      label: 'Administrators',
      value: users.filter((u) => u.administrator).length,
      note: 'Identity service administrators',
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
        <span className="sample-label">Fictional sample data</span>
      </div>
      <div className="stats-grid">
        {stats.map((stat) => (
          <section className="stat-card" key={stat.label}>
            <div>
              <span>{stat.label}</span>
              <stat.icon size={18} />
            </div>
            <strong>{stat.value}</strong>
            <small>{stat.note}</small>
          </section>
        ))}
      </div>
      <div className="overview-grid">
        <section className="panel">
          <div className="panel-heading">
            <div>
              <h2>Recent registrations</h2>
              <p>A few of the newest faces.</p>
            </div>
            <Link to="/users">
              View all <ArrowRight size={14} />
            </Link>
          </div>
          <UserTable data={users.slice(0, 5)} />
        </section>
        <section className="panel attention-panel">
          <div className="panel-heading">
            <div>
              <h2>Needs attention</h2>
              <p>Accounts to keep an eye on.</p>
            </div>
          </div>
          <div className="attention-item">
            <span className="attention-icon">
              <MailCheck size={19} />
            </span>
            <div>
              <strong>{unconfirmed.length} unconfirmed accounts</strong>
              <p>These users haven't confirmed their email addresses yet.</p>
              <Link to="/users?status=Unconfirmed">
                Review accounts <ArrowRight size={14} />
              </Link>
            </div>
          </div>
          <div className="attention-item">
            <span className="attention-icon">
              <ShieldCheck size={19} />
            </span>
            <div>
              <strong>1 locked account</strong>
              <p>A sample account has been temporarily locked.</p>
              <Link to="/users?status=Locked">
                View account <ArrowRight size={14} />
              </Link>
            </div>
          </div>
        </section>
      </div>
      <section className="panel">
        <div className="panel-heading">
          <div>
            <h2>Connected applications</h2>
            <p>A consistent sign-in experience, wherever people work.</p>
          </div>
          <Link to="/clients">
            View clients <ArrowRight size={14} />
          </Link>
        </div>
        <div className="application-grid">
          {applications.map((app) => (
            <div className="application-card" key={app.name}>
              <span className="avatar">{app.initials}</span>
              <div>
                <strong>{app.name}</strong>
                <small>{app.description}</small>
              </div>
            </div>
          ))}
        </div>
      </section>
    </>
  );
}
