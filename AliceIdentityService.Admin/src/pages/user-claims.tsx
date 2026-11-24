import { List, Trash2 } from 'lucide-react';
import { AdminMutationDialog } from '@/components/admin-mutation-dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import type { AdminUserDetail } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

export function UserClaims({
  data,
  onReload,
  onSuccess,
}: {
  data: AdminUserDetail;
  onReload: () => void;
  onSuccess: () => void;
}) {
  const { session } = useAdminSession();
  const path = '/api/admin/users/' + encodeURIComponent(data.user.id) + '/claims';
  const target = data.user.name + ' · ' + (data.user.email || data.user.id);
  const effect =
    'This ends the account’s AIS sessions and OIDC grants. Already issued JWTs may remain valid until expiry.';
  const self = session.user.id === data.user.id;
  const admins = data.claims.filter((claim) => claim.type === 'ais_admin').length;
  return (
    <section className="panel claims-panel" aria-labelledby="user-claims-heading">
      <header className="panel-heading">
        <h2 id="user-claims-heading">
          <List aria-hidden="true" />
          Claims
        </h2>
        <AdminMutationDialog
          title="Add claim"
          description={'Assign a claim type and value. ' + effect}
          target={target}
          path={path}
          input={(fields) => ({
            version: data.version,
            type: fields.get('type'),
            value: fields.get('value'),
          })}
          onSuccess={onSuccess}
          onReload={onReload}
        >
          <Label>
            Claim type
            <Input
              name="type"
              required
              maxLength={256}
              placeholder="department"
              autoComplete="off"
            />
          </Label>
          <Label>
            Claim value
            <Input
              name="value"
              maxLength={4096}
              placeholder="Computer Science"
              autoComplete="off"
            />
          </Label>
          <p className="form-hint">
            The ais_admin claim grants full administration access with any value, including an empty
            value. Identity and protocol claim types are managed by the service.
          </p>
          {self && <p>You will need to sign in again after changing your own claims.</p>}
        </AdminMutationDialog>
        <p>Claims are shared according to each application’s allowed scopes.</p>
      </header>
      {data.claims.length === 0 ? (
        <p className="panel-note">No additional claims.</p>
      ) : (
        <Table className="claim-table" aria-labelledby="user-claims-heading">
          <TableHeader>
            <TableRow>
              <TableHead>Type</TableHead>
              <TableHead>Value</TableHead>
              <TableHead>Action</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data.claims.map((claim) => (
              <TableRow key={claim.id}>
                <TableCell>
                  <code>{claim.type ?? '(No type)'}</code>
                </TableCell>
                <TableCell>
                  <code>{claim.value ?? '(No value)'}</code>
                </TableCell>
                <TableCell>
                  <AdminMutationDialog
                    title="Remove claim"
                    triggerContent={
                      <>
                        <Trash2 aria-hidden="true" />
                        <span className="claim-action-label">Remove</span>
                      </>
                    }
                    description={effect}
                    target={
                      target +
                      ' · ' +
                      (claim.type ?? '(No type)') +
                      ' = ' +
                      (claim.value ?? '(No value)')
                    }
                    path={path + '/' + claim.id}
                    method="DELETE"
                    input={() => ({ version: data.version })}
                    onSuccess={onSuccess}
                    onReload={onReload}
                    disabled={self && claim.type === 'ais_admin' && admins === 1}
                  />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {self && <p className="panel-note">You cannot remove your own last administrator claim.</p>}
    </section>
  );
}
