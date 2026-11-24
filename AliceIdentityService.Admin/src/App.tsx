import { createElement, lazy, Suspense, useState } from 'react';
import {
  BrowserRouter,
  Link,
  matchPath,
  Navigate,
  NavLink,
  Route,
  Routes,
  useLocation,
} from 'react-router-dom';
import {
  AppWindow,
  Fingerprint,
  LayoutDashboard,
  Menu,
  ShieldCheck,
  UserRound,
  Users,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { UserMenu } from '@/components/user-menu';
import { Overview } from '@/pages/overview';
import { AdminAccess } from '@/lib/admin-session';
import { useAdminSession } from '@/lib/admin-context';
import { ScopesPage } from '@/pages/scopes';
const Profile = lazy(() => import('@/pages/profile'));
const UsersPage = lazy(() =>
  import('@/pages/users').then((module) => ({ default: module.UsersPage })),
);
const UserDetail = lazy(() =>
  import('@/pages/users').then((module) => ({ default: module.UserDetail })),
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
  { path: '/profile', label: 'Profile', icon: UserRound },
];
export function AdminShell() {
  const [menuOpen, setMenuOpen] = useState(false);
  const {
    session: { user },
  } = useAdminSession();
  const name =
    user.screenName || `${user.firstName} ${user.lastName}`.trim() || user.email || 'Administrator';
  const initials = (
    user.firstName.charAt(0) + user.lastName.charAt(0) || name.slice(0, 2)
  ).toUpperCase();
  const { pathname, search } = useLocation();
  const section = navigation.find(({ path }) => matchPath({ path, end: path === '/' }, pathname));
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
        <div className="sidebar-identity">
          <span className="avatar" aria-hidden="true">
            {initials}
          </span>
          <div>
            <strong>{name}</strong>
            <small>Administrator</small>
            {user.email && <small>{user.email}</small>}
          </div>
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
            <nav aria-label="Breadcrumb">
              <Link to="/" onClick={() => setMenuOpen(false)}>
                Administration
              </Link>
              <span className="breadcrumb-separator" aria-hidden="true">
                /
              </span>
              <strong aria-current="page">{section?.label ?? 'Page not found'}</strong>
            </nav>
          </div>
          <div className="header-actions">
            {createElement('ais-theme-picker')}
            <UserMenu name={name} initials={initials} email={user.email} />
          </div>
        </header>
        <main id="main" className="main-content">
          <Suspense
            fallback={<p role="status">Loading {section?.label.toLowerCase() ?? 'page'}…</p>}
          >
            <Routes>
              <Route path="/" element={<Overview />} />
              <Route path="/users" element={<UsersPage />} />
              <Route path="/users/:id" element={<UserDetail />} />
              <Route path="/clients" element={<ClientsPage />} />
              <Route path="/clients/:id" element={<ClientDetail />} />
              <Route path="/scopes" element={<ScopesPage />} />
              <Route
                path="/scopes/:id"
                element={<Navigate to={{ pathname: '/scopes', search }} replace />}
              />
              <Route path="/profile" element={<Profile />} />
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
          </Suspense>
        </main>
        <footer className="admin-footer">
          Alice Identity Service 3 · Copyright Chengyu Sun 2026
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
