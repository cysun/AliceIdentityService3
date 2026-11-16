import { useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { AdminMutationError } from '@/lib/admin-api';
import { useAdminSession } from '@/lib/admin-context';

type Props = {
  title: string;
  submitLabel?: string;
  description: string;
  target?: string;
  path: string;
  method?: 'POST' | 'DELETE';
  input: (fields: FormData) => unknown;
  children?: ReactNode;
  confirmation?: string;
  disabled?: boolean;
  onSuccess: (id: string) => void;
  onReload: () => void;
};

export function AdminMutationDialog(props: Props) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
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
            variant={props.method === 'DELETE' ? 'destructive' : 'outline'}
            disabled={props.disabled}
          />
        }
      >
        {props.title}
      </DialogTrigger>
      <DialogContent className="admin-user-dialog" showCloseButton={!busy}>
        <DialogHeader>
          <DialogTitle>{props.title}</DialogTitle>
          <DialogDescription>{props.description}</DialogDescription>
        </DialogHeader>
        {open && <MutationForm {...props} setBusy={setBusy} close={() => setOpen(false)} />}
      </DialogContent>
    </Dialog>
  );
}

function MutationForm({
  setBusy,
  close,
  ...props
}: Props & { setBusy: (value: boolean) => void; close: () => void }) {
  const { mutate } = useAdminSession();
  const inFlight = useRef(false);
  const [pending, setPending] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);
  const [needsReload, setNeedsReload] = useState(false);
  const [confirmation, setConfirmation] = useState('');
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (
      inFlight.current ||
      needsReload ||
      (props.confirmation !== undefined && confirmation !== props.confirmation)
    )
      return;
    const body = props.input(new FormData(event.currentTarget));
    inFlight.current = true;
    setPending(true);
    setBusy(true);
    setErrors([]);
    try {
      const result = await mutate(props.path, body, props.method);
      if (!result.sessionEnded) {
        close();
        props.onSuccess(result.id);
      }
    } catch (error) {
      if (error instanceof AdminMutationError) {
        setErrors(error.messages);
        setNeedsReload(error.status === 409 || error.status === 404);
      }
    } finally {
      inFlight.current = false;
      setPending(false);
      setBusy(false);
    }
  }
  return (
    <form className="admin-user-form" onSubmit={submit}>
      {props.target && <p className="operation-target">{props.target}</p>}
      {errors.length > 0 && (
        <div role="alert" className="operation-errors">
          {errors.map((error, index) => (
            <p key={index}>{error}</p>
          ))}
          {needsReload && (
            <Button
              type="button"
              variant="outline"
              onClick={() => {
                close();
                props.onReload();
              }}
            >
              Reload details
            </Button>
          )}
        </div>
      )}
      <fieldset disabled={pending || needsReload}>
        {props.children}
        {props.confirmation !== undefined && (
          <Label>
            Type {props.confirmation} to confirm
            <Input
              name="confirmation"
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              required
              autoComplete="off"
            />
          </Label>
        )}
      </fieldset>
      <DialogFooter>
        <Button type="button" variant="outline" disabled={pending} onClick={close}>
          Cancel
        </Button>
        <Button
          type="submit"
          variant={props.method === 'DELETE' ? 'destructive' : 'default'}
          disabled={
            pending ||
            needsReload ||
            (props.confirmation !== undefined && confirmation !== props.confirmation)
          }
        >
          {pending ? 'Saving…' : (props.submitLabel ?? props.title)}
        </Button>
      </DialogFooter>
    </form>
  );
}
