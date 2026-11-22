import { useRef, useState, type FormEvent } from 'react';
import { Menu } from '@base-ui/react/menu';
import { LogOut } from 'lucide-react';
import { AdminApiError, getAdminJson } from '@/lib/admin-api';

export function SignOutForm({ inMenu = false }: { inMenu?: boolean }) {
  const tokenInput = useRef<HTMLInputElement>(null);
  const [status, setStatus] = useState<'idle' | 'pending' | 'failed'>('idle');

  async function signOut(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setStatus('pending');
    try {
      const { requestToken } = await getAdminJson<{ requestToken: string }>(
        '/api/account/antiforgery',
      );
      if (tokenInput.current) {
        tokenInput.current.value = requestToken;
        form.submit();
      }
    } catch (error) {
      if (error instanceof AdminApiError && error.status === 401)
        window.location.replace('/Account/Login');
      else setStatus('failed');
    }
  }

  return (
    <form action="/Account/Logout" method="post" className="sign-out-form" onSubmit={signOut}>
      <input type="hidden" name="__RequestVerificationToken" ref={tokenInput} />
      {inMenu ? (
        <Menu.Item
          className="header-menu-item"
          disabled={status === 'pending'}
          closeOnClick={false}
          onClick={(event) => event.currentTarget.closest('form')?.requestSubmit()}
        >
          <LogOut size={16} aria-hidden="true" />
          {status === 'pending' ? 'Signing out…' : 'Sign out'}
        </Menu.Item>
      ) : (
        <button type="submit" disabled={status === 'pending'}>
          Sign out
        </button>
      )}
      {status === 'failed' && <span role="alert">Unable to sign out. Try again.</span>}
    </form>
  );
}
