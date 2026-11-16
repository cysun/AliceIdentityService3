import { lazy, Suspense, useState } from 'react';
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
import { AdminAccess } from '@/lib/admin-session';
import { useAdminSession } from '@/lib/admin-context';
import { initials } from '@/lib/person';
import { ScopeDetail, ScopesPage } from '@/pages/scopes';
import { UserDetail, UsersPage } from '@/pages/users';
const ComponentsPage = lazy(() =>
  import('@/pages/components').then((module) => ({ default: module.ComponentsPage })),
);
const ClientsPage = lazy(() =>
  import('@/pages/clients').then((module) => ({ default: module.ClientsPage })),
);
const ClientDetail = lazy(() =>
  import('@/pages/clients').then((module) => ({ default: module.ClientDetail })),
);
const navigation = [
  { path: '/', label: 'Overview', icon: LayoutDashboard },
  { path: '/users', label: 'Users', icon: Users },
  { path: '/clients', label: 'Clients', icon: AppWindow },
  { path: '/scopes', label: 'Scopes', icon: ShieldCheck },
  { path: '/components', label: 'Components', icon: Blocks },
];
export function AdminShell() {
  const [menuOpen, setMenuOpen] = useState(false);
  const { session } = useAdminSession();
  const name =
    session.user.screenName ||
    `${session.user.firstName} ${session.user.lastName}`.trim() ||
    session.user.email ||
    'Administrator';
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
          <div className="admin-identity">
            <span className="avatar" aria-hidden="true">
              {initials(name)}
            </span>
            <div>
              <strong>{name}</strong>
              <small>Administrator</small>
              <small>{session.user.email}</small>
            </div>
          </div>
          <a href="/Account">
            Your account <ArrowUpRight size={14} />
          </a>
          <a href="/Account/Logout">Sign out</a>
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
          <span className="system-info">
            AIS3 {session.application.version} · Step {session.application.implementationStep}
          </span>
        </header>
        <div className="preview-note">
          <span>
            <strong>Step 13 review</strong> · Staging and hardening are ready for review. AIS2
            migration remains reserved for Step 14.
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
            <Route
              path="/clients"
              element={
                <Suspense fallback={<p>Loading clients…</p>}>
                  <ClientsPage />
                </Suspense>
              }
            />
            <Route
              path="/clients/:id"
              element={
                <Suspense fallback={<p>Loading client…</p>}>
                  <ClientDetail />
                </Suspense>
              }
            />
            <Route path="/scopes" element={<ScopesPage />} />
            <Route path="/scopes/:id" element={<ScopeDetail />} />
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
      <AdminAccess>
        <AdminShell />
      </AdminAccess>
    </BrowserRouter>
  );
}
