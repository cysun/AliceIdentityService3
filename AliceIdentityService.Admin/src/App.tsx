import { lazy, Suspense, useEffect, useState } from 'react';
import { BrowserRouter, Link, NavLink, Route, Routes } from 'react-router-dom';
import {
  AppWindow,
  ArrowUpRight,
  Blocks,
  Fingerprint,
  LayoutDashboard,
  Menu,
  ShieldCheck,
  Users,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Overview } from '@/pages/overview';
import { UserDetail, UsersPage } from '@/pages/users';
const ComponentsPage = lazy(() =>
  import('@/pages/components').then((module) => ({ default: module.ComponentsPage })),
);
const navigation = [
  { path: '/', label: 'Overview', icon: LayoutDashboard },
  { path: '/users', label: 'Users', icon: Users },
  { path: '/clients', label: 'Clients', icon: AppWindow },
  { path: '/scopes', label: 'Scopes', icon: ShieldCheck },
  { path: '/components', label: 'Components', icon: Blocks },
];
function SystemInfo() {
  const [info, setInfo] = useState('Connecting to local API…');
  useEffect(() => {
    const controller = new AbortController();
    fetch('/api/system/info', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error('API unavailable');
        const data: { name: string; version: string; implementationStep: number } =
          await response.json();
        setInfo(`AIS3 ${data.version} · Step ${data.implementationStep}`);
      })
      .catch((error) => {
        if (error.name !== 'AbortError') setInfo('Local API unavailable');
      });
    return () => controller.abort();
  }, []);
  return (
    <span className="system-info" role="status">
      {info}
    </span>
  );
}
function PlannedPage({ title, step }: { title: string; step: number }) {
  return (
    <>
      <div className="page-heading">
        <div>
          <h1>{title}</h1>
          <p>Application access, managed in one place.</p>
        </div>
      </div>
      <section className="panel detail-panel">
        <h2>Coming in Step {step}</h2>
        <p>{title} management is reserved for its own implementation and review step.</p>
        <Link to="/components">Explore the component preview →</Link>
      </section>
    </>
  );
}
export function AdminShell() {
  const [menuOpen, setMenuOpen] = useState(false);
  return (
    <div className="admin-layout">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <aside className={`sidebar ${menuOpen ? 'menu-open' : ''}`}>
        <Link className="brand" to="/">
          <span className="brand-mark">
            <Fingerprint size={25} />
          </span>
          <span>
            Alice Identity<small>Administration</small>
          </span>
        </Link>
        <div className="nav-caption">Workspace</div>
        <nav aria-label="Administration">
          {navigation.map((item) => (
            <NavLink
              key={item.path}
              to={item.path}
              end={item.path === '/'}
              onClick={() => setMenuOpen(false)}
            >
              <item.icon size={18} />
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="preview-identity">
            <span className="avatar">AM</span>
            <div>
              <strong>Alex Morgan</strong>
              <small>Sample administrator</small>
            </div>
          </div>
          <a href="/Account">
            Your account / sign out <ArrowUpRight size={14} />
          </a>
        </div>
      </aside>
      <div className="admin-body">
        <header className="topbar">
          <div className="topbar-start">
            <Button
              className="mobile-menu"
              variant="ghost"
              size="icon"
              aria-label="Toggle navigation"
              aria-expanded={menuOpen}
              onClick={() => setMenuOpen(!menuOpen)}
            >
              <Menu />
            </Button>
            <span>
              Administration <span className="breadcrumb-separator">/</span>{' '}
              <strong>Alice Identity Service</strong>
            </span>
          </div>
          <SystemInfo />
        </header>
        <div className="preview-note">
          <span>
            <strong>Step 4 preview</strong> · Administrator access required. Data and identity below
            are samples.
          </span>
          <div>
            <a href="/Account/Login">Direct login</a>
          </div>
        </div>
        <main id="main" className="main-content">
          <Routes>
            <Route path="/" element={<Overview />} />
            <Route path="/users" element={<UsersPage />} />
            <Route path="/users/:id" element={<UserDetail />} />
            <Route path="/clients" element={<PlannedPage title="Clients" step={11} />} />
            <Route path="/scopes" element={<PlannedPage title="Scopes" step={10} />} />
            <Route
              path="/components"
              element={
                <Suspense fallback={<p>Loading component preview…</p>}>
                  <ComponentsPage />
                </Suspense>
              }
            />
            <Route
              path="*"
              element={
                <>
                  <h1>Page not found</h1>
                  <Link to="/">Return to overview</Link>
                </>
              }
            />
          </Routes>
        </main>
        <footer className="admin-footer">
          Alice Identity Service 3 <span>Built around your identity.</span>
        </footer>
      </div>
    </div>
  );
}
export default function App() {
  return (
    <BrowserRouter basename="/admin">
      <AdminShell />
    </BrowserRouter>
  );
}
