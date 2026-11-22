import { useEffect, useState } from 'react';
import { cn } from 'cn';
import { buttonVariants } from '@/components/ui/button';
import type { AdminUserDetail } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';
import { UserOperationButton } from '@/pages/user-operations';

export default function Profile() {
  const { session, request } = useAdminSession();
  const [details, setDetails] = useState<AdminUserDetail>();
  const [revision, setRevision] = useState(0);
  const [notice, setNotice] = useState('');
  const id = session.user.id;

  useEffect(() => {
    const controller = new AbortController();
    request<AdminUserDetail>('/api/admin/users/' + encodeURIComponent(id), controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setDetails(data);
      })
      .catch(() => {
        /* AdminAccess handles request failures. */
      });
    return () => controller.abort();
  }, [id, revision, request]);

  function reload() {
    setDetails(undefined);
    setRevision((current) => current + 1);
  }

  return (
    <>
      <div className="page-heading">
        <h1>Profile</h1>
        {details && (
          <UserOperationButton
            operation="profile"
            data={details}
            onReload={reload}
            onSuccess={() => {
              setNotice('Profile updated.');
              reload();
            }}
          />
        )}
      </div>
      {notice && (
        <p role="status" className="operation-notice">
          {notice}
        </p>
      )}
      {details ? (
        <section className="panel detail-panel">
          <h2>Account details</h2>
          <dl className="detail-list">
            <div>
              <dt>First name</dt>
              <dd>{details.firstName}</dd>
            </div>
            <div>
              <dt>Last name</dt>
              <dd>{details.lastName}</dd>
            </div>
            <div>
              <dt>Screen name</dt>
              <dd>{details.screenName}</dd>
            </div>
            <div>
              <dt>Email address</dt>
              <dd>{details.user.email || 'No email address'}</dd>
            </div>
          </dl>
          <div className="security-actions">
            <a
              className={cn(buttonVariants({ variant: 'outline' }))}
              href="/Account/ChangePassword"
            >
              Change password
            </a>
          </div>
        </section>
      ) : (
        <p role="status">Loading profile…</p>
      )}
    </>
  );
}
