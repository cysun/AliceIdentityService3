import { useRef, useState, type FormEvent } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { AdminMutationError, type AdminUserDetail } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

type Operation = 'create' | 'edit' | 'profile' | 'password' | 'lock' | 'unlock' | 'delete';
const titles: Record<Operation, string> = {
  create: 'Create account',
  edit: 'Edit account',
  profile: 'Edit profile',
  password: 'Reset password',
  lock: 'Lock account',
  unlock: 'Unlock account',
  delete: 'Delete account',
};
const messages: Record<Operation, string> = {
  create:
    'Create an ordinary, confirmed account after verifying its email address. No email is sent and no administrator claim is assigned.',
  edit: 'Update names and email confirmation. Changing confirmation ends this account’s AIS sessions and OIDC grants. The email address stays unchanged.',
  profile: 'Update your name and the screen name shown in connected apps.',
  password:
    'Set a new password. This ends the account’s AIS sessions and OIDC grants. Lockout and two-factor settings stay in place. No email is sent.',
  lock: 'Prevent sign-in for the selected duration. Existing AIS sessions and OIDC grants will be invalidated.',
  unlock:
    'Clear the lockout and failed sign-in count. Previous sessions will remain invalid; the user must sign in again.',
  delete:
    'Permanently delete this account and its Identity data. OIDC grants will be revoked. This cannot be undone.',
};
const durations = [
  { label: '1 hour', value: 'hour' },
  { label: '24 hours', value: 'day' },
  { label: 'Until unlocked', value: 'indefinite' },
];

export function UserOperationButton({
  operation,
  data,
  onSuccess,
  onReload,
}: {
  operation: Operation;
  data?: AdminUserDetail;
  onSuccess: (id: string) => void;
  onReload?: () => void;
}) {
  const { session } = useAdminSession();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const self = data?.user.id === session.user.id;
  const disabled = self && (operation === 'lock' || operation === 'delete');
  return (
    <Dialog
      open={open}
      onOpenChange={(value) => {
        if (!busy) setOpen(value);
      }}
    >
      <DialogTrigger
        render={
          <Button
            variant={
              operation === 'delete'
                ? 'destructive'
                : operation === 'create'
                  ? 'default'
                  : 'outline'
            }
            disabled={disabled}
            title={disabled ? 'You cannot lock or delete your own account.' : undefined}
          />
        }
      >
        {titles[operation]}
      </DialogTrigger>
      <DialogContent className="admin-user-dialog" showCloseButton={!busy}>
        <DialogHeader>
          <DialogTitle>{titles[operation]}</DialogTitle>
          <DialogDescription>{messages[operation]}</DialogDescription>
        </DialogHeader>
        {open && (
          <OperationForm
            operation={operation}
            data={data}
            self={self}
            setBusy={setBusy}
            onClose={() => setOpen(false)}
            onSuccess={(id) => {
              setOpen(false);
              onSuccess(id);
            }}
            onReload={() => {
              setOpen(false);
              onReload?.();
            }}
          />
        )}
      </DialogContent>
    </Dialog>
  );
}

function OperationForm({
  operation,
  data,
  self,
  setBusy,
  onClose,
  onSuccess,
  onReload,
}: {
  operation: Operation;
  data?: AdminUserDetail;
  self: boolean;
  setBusy: (busy: boolean) => void;
  onClose: () => void;
  onSuccess: (id: string) => void;
  onReload: () => void;
}) {
  const { mutate } = useAdminSession();
  const [pending, setPending] = useState(false);
  const inFlight = useRef(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [needsReload, setNeedsReload] = useState(false);
  const [confirmed, setConfirmed] = useState(data?.emailConfirmed ?? true);
  const [duration, setDuration] = useState('day');
  const [confirmation, setConfirmation] = useState('');
  const identifier = data?.user.email?.trim() ? data.user.email : (data?.user.id ?? '');
  const deleting = operation === 'delete';
  const editing = operation === 'edit' || operation === 'profile';
  const names = editing || operation === 'create';
  const password = operation === 'password' || operation === 'create';
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (inFlight.current || needsReload) return;
    const fields = new FormData(event.currentTarget);
    if (password && fields.get('password') !== fields.get('confirmPassword')) {
      setErrors(['The passwords do not match.']);
      return;
    }
    const input: Record<string, unknown> = operation === 'create' ? {} : { version: data!.version };
    if (names)
      for (const name of ['firstName', 'lastName', 'screenName'])
        input[name] = String(fields.get(name) ?? '').trim();
    if (operation === 'create') input.email = String(fields.get('email') ?? '').trim();
    if (editing) input.emailConfirmed = confirmed;
    if (password) {
      input.password = fields.get('password');
      input.confirmPassword = fields.get('confirmPassword');
    }
    if (operation === 'lock') input.duration = duration;
    if (deleting) input.confirmation = confirmation;
    const suffix = editing ? 'profile' : operation;
    const path =
      operation === 'create'
        ? '/api/admin/users'
        : '/api/admin/users/' + encodeURIComponent(data!.user.id) + (deleting ? '' : '/' + suffix);
    inFlight.current = true;
    setPending(true);
    setBusy(true);
    setErrors([]);
    try {
      const result = await mutate(path, input, deleting ? 'DELETE' : 'POST');
      if (!result.sessionEnded) onSuccess(result.id);
    } catch (error) {
      if (error instanceof AdminMutationError) {
        setErrors(error.messages);
        setNeedsReload(error.status === 409 || error.status === 404);
      }
      // Authentication and uncertain network/server outcomes clear the protected shell.
    } finally {
      inFlight.current = false;
      setPending(false);
      setBusy(false);
    }
  }
  return (
    <form className="admin-user-form" onSubmit={submit}>
      {data && (
        <p className="operation-target">
          <strong>{data.user.name}</strong>
          <br />
          {identifier}
        </p>
      )}
      {errors.length > 0 && (
        <div role="alert" className="operation-errors">
          {errors.map((error, index) => (
            <p key={index}>{error}</p>
          ))}
          {needsReload && (
            <Button type="button" variant="outline" onClick={onReload}>
              Reload account
            </Button>
          )}
        </div>
      )}
      <fieldset disabled={pending || needsReload}>
        {names && (
          <>
            <Label>
              First name
              <Input
                name="firstName"
                defaultValue={data?.firstName}
                required
                maxLength={255}
                autoComplete="off"
              />
            </Label>
            <Label>
              Last name
              <Input
                name="lastName"
                defaultValue={data?.lastName}
                required
                maxLength={255}
                autoComplete="off"
              />
            </Label>
            <Label>
              Screen name
              <Input
                name="screenName"
                defaultValue={data?.screenName}
                required
                maxLength={255}
                autoComplete="off"
              />
            </Label>
          </>
        )}
        {operation === 'create' && (
          <Label>
            Email address
            <Input name="email" type="email" required maxLength={256} autoComplete="off" />
          </Label>
        )}
        {operation === 'edit' && (
          <label className="confirmation-check">
            <Checkbox
              checked={confirmed}
              onCheckedChange={setConfirmed}
              disabled={self || pending || needsReload}
            />
            Email confirmed
          </label>
        )}
        {password && (
          <>
            <Label>
              New password
              <Input
                name="password"
                type="password"
                required
                maxLength={4096}
                autoComplete="new-password"
              />
            </Label>
            <Label>
              Confirm new password
              <Input
                name="confirmPassword"
                type="password"
                required
                maxLength={4096}
                autoComplete="new-password"
              />
            </Label>
            <p className="form-hint">
              Use at least six characters, including uppercase, lowercase, a digit and a symbol.
            </p>
            {self && <p>You will need to sign in again after resetting your password.</p>}
          </>
        )}
        {operation === 'lock' && (
          <div>
            <Label>Lock duration</Label>
            <Select
              value={duration}
              disabled={pending || needsReload}
              items={durations}
              onValueChange={(value) => {
                if (value) setDuration(value);
              }}
            >
              <SelectTrigger aria-label="Lock duration">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {durations.map((item) => (
                  <SelectItem key={item.value} value={item.value}>
                    {item.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
        {deleting && (
          <Label>
            Type {identifier} to confirm
            <Input
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              required
              autoComplete="off"
            />
          </Label>
        )}
      </fieldset>
      <DialogFooter>
        <Button type="button" variant="outline" disabled={pending} onClick={onClose}>
          Cancel
        </Button>
        <Button
          type="submit"
          variant={deleting ? 'destructive' : 'default'}
          disabled={pending || needsReload || (deleting && confirmation.trim() !== identifier)}
        >
          {pending ? 'Saving…' : editing ? 'Save changes' : titles[operation]}
        </Button>
      </DialogFooter>
    </form>
  );
}
