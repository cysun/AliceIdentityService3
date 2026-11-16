import { Fragment, useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import {
  AdminApiError,
  AdminMutationError,
  getAdminJson,
  mutateAdminJson,
  type AdminSession,
} from './admin-api';
import { AdminSessionContext } from './admin-context';

type Blocked = 'expired' | 'forbidden' | 'unavailable';
type State =
  | { status: 'checking' | Blocked; uncertainWrite?: boolean }
  | { status: 'ready'; session: AdminSession; remainingMs: number };
function failureStatus(error: unknown): Blocked {
  if (error instanceof AdminApiError && error.status === 401) return 'expired';
  if (error instanceof AdminApiError && error.status === 403) return 'forbidden';
  return 'unavailable';
}

export function AdminAccess({ children }: { children: ReactNode }) {
  const [state, setState] = useState<State>({ status: 'checking' });
  const pending = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const location = useLocation();

  const cancelCheck = useCallback(() => {
    generation.current++;
    pending.current?.abort();
  }, []);
  const block = useCallback(
    (status: Blocked, uncertainWrite = false) => {
      cancelCheck();
      setState({ status, uncertainWrite });
    },
    [cancelCheck],
  );

  const check = useCallback(async () => {
    pending.current?.abort();
    const controller = new AbortController();
    pending.current = controller;
    const current = ++generation.current;
    try {
      const session = await getAdminJson<AdminSession>('/api/admin/session', controller.signal);
      const remainingMs = Date.parse(session.expiresAt) - Date.parse(session.serverTime);
      if (!Number.isFinite(remainingMs)) throw new Error('Invalid session expiry');
      if (controller.signal.aborted || generation.current !== current) return;
      if (remainingMs <= 0) block('expired');
      else setState({ status: 'ready', session, remainingMs });
    } catch (error) {
      if (!controller.signal.aborted && generation.current === current) block(failureStatus(error));
    }
  }, [block]);

  const request = useCallback(
    async <T,>(path: string, signal?: AbortSignal, options?: { allowNotFound?: boolean }) => {
      try {
        return await getAdminJson<T>(path, signal);
      } catch (error) {
        if (
          !signal?.aborted &&
          !(options?.allowNotFound && error instanceof AdminApiError && error.status === 404)
        )
          block(failureStatus(error));
        throw error;
      }
    },
    [block],
  );

  const actorId = state.status === 'ready' ? state.session.user.id : '';
  const mutate = useCallback(
    async (path: string, body: unknown, method?: 'POST' | 'DELETE') => {
      try {
        const result = await mutateAdminJson(path, body, actorId, method);
        if (result.sessionEnded) block('expired');
        else void check();
        return result;
      } catch (error) {
        // Validation, stale edits and removed records belong to the form. Never replay a write.
        if (!(error instanceof AdminMutationError && [400, 404, 409].includes(error.status)))
          block(failureStatus(error), true);
        throw error;
      }
    },
    [block, check, actorId],
  );

  useEffect(() => {
    // check only updates state after its network request completes.
    // oxlint-disable-next-line react/set-state-in-effect
    void check();
    const checkVisible = () => {
      if (document.visibilityState !== 'hidden') void check();
    };
    window.addEventListener('focus', checkVisible);
    window.addEventListener('pageshow', checkVisible);
    document.addEventListener('visibilitychange', checkVisible);
    const interval = window.setInterval(checkVisible, 60_000);
    return () => {
      window.clearInterval(interval);
      window.removeEventListener('focus', checkVisible);
      window.removeEventListener('pageshow', checkVisible);
      document.removeEventListener('visibilitychange', checkVisible);
      cancelCheck();
    };
  }, [check, cancelCheck, location.key]);

  useEffect(() => {
    if (state.status !== 'ready') return;
    // Use the server's remaining lifetime rather than the workstation's wall clock.
    // Background polling never extends the cookie's fixed expiration.
    const timer = window.setTimeout(() => block('expired'), state.remainingMs);
    return () => window.clearTimeout(timer);
  }, [state, block]);

  if (state.status === 'ready')
    return (
      <AdminSessionContext.Provider value={{ session: state.session, request, mutate }}>
        <Fragment key={state.session.user.id}>{children}</Fragment>
      </AdminSessionContext.Provider>
    );

  const titles = {
    checking: 'Checking your session…',
    expired: 'Please sign in again',
    forbidden: 'Administrator access required',
    unavailable: 'Administration is temporarily unavailable',
  };
  const returnUrl = '/admin' + location.pathname + location.search;
  return (
    <main className="admin-access" aria-labelledby="access-heading">
      <section
        className="panel detail-panel"
        role={state.status === 'checking' ? 'status' : 'alert'}
      >
        <h1 id="access-heading">{titles[state.status]}</h1>
        {state.status === 'expired' && (
          <>
            <p>Your session has ended. Sign in to return to this page.</p>
            <a href={'/Account/Login?returnUrl=' + encodeURIComponent(returnUrl)}>Sign in</a>
          </>
        )}
        {state.status === 'forbidden' && (
          <>
            <p>Your account does not currently have permission to use administration.</p>
            <a href="/Account">Your account</a>
            {' · '}
            <a href="/Account/Logout">Sign out</a>
          </>
        )}
        {state.status === 'unavailable' && (
          <>
            <p>We could not verify access or load administration data. Please try again.</p>
            {state.uncertainWrite && (
              <p>
                The change may have completed. Reload and check the account before submitting again.
              </p>
            )}
            <Button
              onClick={() => {
                setState({ status: 'checking' });
                void check();
              }}
            >
              Try again
            </Button>
          </>
        )}
      </section>
    </main>
  );
}
